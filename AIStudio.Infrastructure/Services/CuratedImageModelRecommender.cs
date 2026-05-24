using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// MVP implementace <see cref="IImageModelRecommender"/> — používá hardcoded
/// <see cref="RecommendedModels"/> katalog. Live Civitai/HuggingFace fallback
/// přijde v další fázi.
///
/// <para>Logika:</para>
/// <list type="number">
/// <item>Najde nejlepší lokální match přes <see cref="IImageModelMatcher"/>.</item>
/// <item>Zjistí ideal pro daný kind přes <see cref="RecommendedModels.BestImageForKind"/>.</item>
/// <item>Pokud ideal je null (kind nemá curated picky) → no upgrade.</item>
/// <item>Pokud uživatel už ideal má lokálně (file name match) → no upgrade.</item>
/// <item>Pokud uživatel už má lepší model než SD 1.5 a ideal je DreamShaper (SD 1.5)
///   → no upgrade (downgrade by byl nesmysl).</item>
/// <item>Jinak → vrátíme <see cref="ModelUpgradeOffer"/>.</item>
/// </list>
/// </summary>
public sealed class CuratedImageModelRecommender : IImageModelRecommender
{
    private readonly IImageModelMatcher _matcher;

    public CuratedImageModelRecommender(IImageModelMatcher matcher)
    {
        _matcher = matcher;
    }

    public Task<ImageModelRecommendation> RecommendAsync(
        ImageIntent           intent,
        IReadOnlyList<string> localCheckpoints,
        CancellationToken     ct)
    {
        var localMatch = _matcher.Match(intent.Kind, localCheckpoints);
        var ideal      = RecommendedModels.BestImageForKind(intent.Kind);

        // Není curated picky pro tento kind → vrátíme jen lokální match
        if (ideal is null)
            return Task.FromResult(new ImageModelRecommendation(localMatch, Upgrade: null));

        // Má uživatel ideal už lokálně? Match po filename (case insensitive).
        // Bereme i "obsahuje", aby fungovalo i pro varianty (Q4 / Q5 / fp8).
        var idealStem = Path.GetFileNameWithoutExtension(ideal.FileName);
        var hasIdeal  = localCheckpoints.Any(name =>
            name.Equals(ideal.FileName, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(idealStem,    StringComparison.OrdinalIgnoreCase));

        if (hasIdeal)
            return Task.FromResult(new ImageModelRecommendation(localMatch, Upgrade: null));

        // Sanity guard: pokud ideal je DreamShaper (SD 1.5, low tier) a uživatel
        // už má SDXL / FLUX lokálně, nenabídneme downgrade.
        if (ideal.Id == RecommendedModels.DreamShaper8_Sd15.Id &&
            localCheckpoints.Any(IsLikelyHigherTierThanSd15))
            return Task.FromResult(new ImageModelRecommendation(localMatch, Upgrade: null));

        var offer = new ModelUpgradeOffer(
            Id:                       ideal.Id,
            Name:                     ideal.Name,
            Reason:                   ReasonForKind(intent.Kind, ideal),
            SizeBytes:                ideal.SizeBytes,
            DownloadUrl:              ideal.DownloadUrl,
            FileName:                 ideal.FileName,
            Sha256:                   ideal.Sha256,
            RequiresHuggingFaceToken: ideal.RequiresHuggingFaceToken,
            Kind:                     intent.Kind);

        return Task.FromResult(new ImageModelRecommendation(localMatch, offer));
    }

    /// <summary>
    /// Heuristika: pokud filename obsahuje "xl" / "flux" / "sdxl" → predpokládáme
    /// vyšší tier než SD 1.5. Pro detekci downgrade scénářů.
    /// </summary>
    private static bool IsLikelyHigherTierThanSd15(string filename)
    {
        var lower = filename.ToLowerInvariant();
        return lower.Contains("xl") || lower.Contains("flux") || lower.Contains("sd3");
    }

    /// <summary>Jedno-věty popis proč ten model je dobrá volba pro daný kind.</summary>
    private static string ReasonForKind(ImageKind kind, RecommendedModel ideal) => kind switch
    {
        ImageKind.Realistic => $"{ideal.Name} dává výrazně lepší foto-realistické výsledky než SD 1.5",
        ImageKind.Abstract  => $"{ideal.Name} je vhodný pro kreativní / abstraktní scény",
        ImageKind.Stylized  => $"{ideal.Name} je vyladěný pro stylizovaný digital paint",
        ImageKind.Auto      => $"{ideal.Name} je univerzální top-tier model",
        _                   => $"{ideal.Name} je doporučená volba",
    };
}
