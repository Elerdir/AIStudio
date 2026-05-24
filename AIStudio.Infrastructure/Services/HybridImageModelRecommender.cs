using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Hybrid recommender — kombinuje rychlý curated katalog s live Civitai/HF
/// search jako fallback. Strategie:
///
/// <list type="number">
/// <item>Zkusíme curated (instantní, deterministické).</item>
/// <item>Pokud curated má upgrade nabídku → vrátíme ji (curated má prioritu,
///   je předvídatelný a má důvěryhodné URL/checksumy).</item>
/// <item>Pokud curated nemá upgrade ALE uživatel nemá ŽÁDNÝ relevantní
///   lokální model → fallback na live search (Civitai pro Anime/Realistic,
///   HF pro FLUX/abstract).</item>
/// <item>Pokud curated nemá upgrade ALE má LocalBestMatch → no upgrade
///   (uživatel má něco rozumného, nehážeme mu na hlavu live nabídky).</item>
/// </list>
///
/// <para>Live search už cachuje <see cref="IModelDiscoveryService"/> (24 h
/// v procesu), takže neházíme requesty na API při každé zprávě.</para>
/// </summary>
public sealed class HybridImageModelRecommender : IImageModelRecommender
{
    private readonly IImageModelRecommender _curated;
    private readonly IModelDiscoveryService _discovery;
    private readonly ISettingsService       _settings;

    public HybridImageModelRecommender(
        IImageModelRecommender curated,
        IModelDiscoveryService discovery,
        ISettingsService       settings)
    {
        _curated   = curated;
        _discovery = discovery;
        _settings  = settings;
    }

    public async Task<ImageModelRecommendation> RecommendAsync(
        ImageIntent           intent,
        IReadOnlyList<string> localCheckpoints,
        CancellationToken     ct)
    {
        // 0) Uživatel řekl "už mě neptej pro tento kind"? Vrátíme jen lokální
        // match z curated bez upgrade nabídky. Settings.IgnoredImageUpgradeKinds
        // se naplní z UI checkboxu "Už mi to nenavrhuj pro tento typ".
        if (_settings.Settings.IgnoredImageUpgradeKinds.Contains(intent.Kind.ToString()))
        {
            var curatedSilent = await _curated.RecommendAsync(intent, localCheckpoints, ct);
            return new ImageModelRecommendation(curatedSilent.LocalBestMatch, Upgrade: null);
        }

        // 1) Curated má prioritu — když najde, vracíme rovnou.
        var curated = await _curated.RecommendAsync(intent, localCheckpoints, ct);
        if (curated.Upgrade is not null)
            return curated;

        // 2) Curated nic nenavrhuje a user má lokální match → no upgrade.
        if (curated.LocalBestMatch is not null)
            return curated;

        // 3) Nemá nic lokální → zkusíme live search.
        try
        {
            var liveOffer = await TrySearchLiveAsync(intent, localCheckpoints, ct);
            if (liveOffer is not null)
            {
                Log.Information("HybridImageModelRecommender: live found {Name} ({Source})",
                                liveOffer.Name, liveOffer.Id);
                return new ImageModelRecommendation(LocalBestMatch: null, Upgrade: liveOffer);
            }
        }
        catch (OperationCanceledException)
        {
            throw;  // Stop tlačítko musí prorazit
        }
        catch (Exception ex)
        {
            // Live search není kritický — pokud selže (rate limit, offline), prostě nedoporučíme.
            Log.Warning(ex, "HybridImageModelRecommender: live search selhal, padáme bez upgrade");
        }

        return curated;  // no upgrade
    }

    /// <summary>
    /// Mapuje kind na search parametry a vytáhne top-1 model. Vrátí
    /// <see cref="ModelUpgradeOffer"/> pokud něco vhodného našel a uživatel
    /// to ještě nemá lokálně.
    /// </summary>
    private async Task<ModelUpgradeOffer?> TrySearchLiveAsync(
        ImageIntent           intent,
        IReadOnlyList<string> localCheckpoints,
        CancellationToken     ct)
    {
        var (provider, query) = MapKindToSearch(intent.Kind);

        var results = await _discovery.SearchAsync(
            provider:    provider,
            query:       query,
            kind:        PickKind.Image,
            includeNsfw: false,
            limit:       5,
            ct:          ct);

        if (results.Count == 0) return null;

        // Vezmeme top-1 podle počtu stažení (popularita = rozumný heuristic).
        // Civitai už defaultně řadí podle MostDownloaded, ale ne všechny
        // implementace; explicit OrderByDescending je defenzivní.
        var top = results.OrderByDescending(r => r.Downloads).First();

        // Pokud uživatel už ten model náhodou má (stejný filename), ignorujeme.
        if (localCheckpoints.Any(c => c.Equals(top.FileName, StringComparison.OrdinalIgnoreCase)))
            return null;

        // Velikost 0 znamená, že API velikost nevrátil — nezarazíme se,
        // jen UI ukáže "? MB" místo přesné hodnoty.
        return new ModelUpgradeOffer(
            Id:                       $"live-{top.Provider}-{top.ProviderRef}",
            Name:                     top.Name,
            Reason:                   BuildLiveReason(intent.Kind, top),
            SizeBytes:                top.SizeBytes,
            DownloadUrl:              top.DownloadUrl,
            FileName:                 top.FileName,
            Sha256:                   top.Sha256,
            // Většina image modelů na HF i Civitai je public — token není potřeba.
            // Gated repa (Llama 3, Gemma…) jsou výjimkou a discovery API nám neříká,
            // jestli je repo gated. Pokud download vrátí 401, DownloadService to
            // zaloguje a UI ukáže chybu. Default false je správný pro 95 % případů.
            RequiresHuggingFaceToken: false,
            Kind:                     intent.Kind);
    }

    /// <summary>
    /// Mapuje image kind na search query + provider. Cíl: pro každý kind
    /// najít realistický top-1 model, který by uživatel pravděpodobně chtěl.
    /// </summary>
    private static (PickProvider Provider, string Query) MapKindToSearch(ImageKind kind) => kind switch
    {
        ImageKind.Realistic => (PickProvider.Civitai,     "realistic photorealistic"),
        ImageKind.Anime     => (PickProvider.Civitai,     "anime"),
        ImageKind.Stylized  => (PickProvider.Civitai,     "stylized digital art"),
        ImageKind.Abstract  => (PickProvider.HuggingFace, "flux"),
        _                   => (PickProvider.HuggingFace, "flux"),
    };

    private static string BuildLiveReason(ImageKind kind, DiscoveredModel top)
    {
        var dl = top.Downloads >= 1000
            ? $"{top.Downloads / 1000}k downloads"
            : $"{top.Downloads} downloads";
        var kindLabel = kind switch
        {
            ImageKind.Anime     => "anime",
            ImageKind.Realistic => "fotorealistický",
            ImageKind.Stylized  => "stylizovaný",
            ImageKind.Abstract  => "abstraktní/kreativní",
            _                   => "univerzální",
        };
        return $"Nejstahovanější {kindLabel} model z {top.Provider} ({dl})";
    }
}
