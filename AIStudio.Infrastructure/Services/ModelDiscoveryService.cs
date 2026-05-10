using System.Collections.Concurrent;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Discovery service — drží statický seznam Picks (curated kategorie pro tab
/// „Doporučené") a cache výsledků. Aggreguje volání HF + Civitai do jednotného
/// <see cref="DiscoveredModel"/> typu, takže UI nemusí znát rozdíl.
/// </summary>
public sealed class ModelDiscoveryService : IModelDiscoveryService
{
    private readonly IHuggingFaceClient _hf;
    private readonly ICivitaiClient     _civ;

    /// <summary>In-memory cache. Klíč = <c>Pick.Id</c>, hodnota = (timestamp, models).</summary>
    private readonly ConcurrentDictionary<string, (DateTime FetchedAt, IReadOnlyList<DiscoveredModel> Models)> _cache = new();

    /// <summary>Pickaný TTL — Civitai i HF top-N se mění pomalu, 24 h je rozumný kompromis.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public ModelDiscoveryService(IHuggingFaceClient hf, ICivitaiClient civ)
    {
        _hf  = hf;
        _civ = civ;
    }

    // ── Curated Picks ─────────────────────────────────────────────────────────
    //
    // Recepty pro „Doporučené" tab. Každý Pick obsahuje query a filtry, ze kterých
    // discovery service získá top-N reálně existujících modelů. Pořadí v listu
    // = pořadí sekcí v UI.
    //
    // HF: pro chat modely chceme GGUF (LLamaSharp je umí inferovat). Pro různé
    // niky (general / kreativní / kód) měníme query string.
    //
    // Civitai: pro image checkpointy preferujeme baseModel filter, aby SDXL šel
    // do své sekce a SD 1.5 / FLUX do své. LoRA sekce je samostatná.
    //
    public IReadOnlyList<ModelPick> Picks { get; } = new[]
    {
        // ── Chat ────────────────────────────────────────────────────────────────
        // Pro chat modely posíláme jen filter=gguf (bez pipeline_tag) — GGUF repos
        // mají často nesetované pipeline_tag, čímž by hodně modelů spadlo pod stůl.
        // Sort=downloads+desc tak přirozeně dostává top-N stahovaných GGUF repos.
        new ModelPick(
            Id:       "chat-general-7-8b",
            Title:    "Chat — obecné 7B/8B (GGUF)",
            Hint:     "Univerzální asistenti střední velikosti. Vybalancované mezi rychlostí a kvalitou. Doporučeno pro 8 GB+ VRAM.",
            Kind:     PickKind.Chat,
            Provider: PickProvider.HuggingFace,
            Query:    "8B instruct gguf",
            HfFilter: "gguf"),

        new ModelPick(
            Id:       "chat-creative-rp",
            Title:    "Chat — kreativní psaní / RP",
            Hint:     "Modely vyladěné na storytelling, RP, dialog. Méně cenzurovat, víc charakter.",
            Kind:     PickKind.Chat,
            Provider: PickProvider.HuggingFace,
            Query:    "roleplay gguf",
            HfFilter: "gguf"),

        new ModelPick(
            Id:       "chat-code",
            Title:    "Chat — programování",
            Hint:     "Specializované na kód a vysvětlování — DeepSeek-Coder, Qwen-Coder, StarCoder a spol.",
            Kind:     PickKind.Chat,
            Provider: PickProvider.HuggingFace,
            Query:    "coder gguf",
            HfFilter: "gguf"),

        new ModelPick(
            Id:       "chat-large",
            Title:    "Chat — velké (13B+)",
            Hint:     "Modely 13B–34B. Vyžadují 16 GB+ VRAM nebo offload do RAM. Lepší reasoning, pomalejší.",
            Kind:     PickKind.Chat,
            Provider: PickProvider.HuggingFace,
            Query:    "13B gguf",
            HfFilter: "gguf"),

        // ── Image (Civitai) ─────────────────────────────────────────────────────
        new ModelPick(
            Id:               "image-sdxl-realistic",
            Title:            "Image — SDXL fotorealismus",
            Hint:             "Top SDXL checkpointy zaměřené na realistické portréty a scény. .safetensors, ~6.5 GB.",
            Kind:             PickKind.Image,
            Provider:         PickProvider.Civitai,
            CivitaiType:      "Checkpoint",
            CivitaiBaseModel: "SDXL 1.0"),

        new ModelPick(
            Id:               "image-pony",
            Title:            "Image — Pony / ilustrace / anime",
            Hint:             "Pony Diffusion + derivací — top-tier pro stylizovanou tvorbu, anime, ilustrace.",
            Kind:             PickKind.Image,
            Provider:         PickProvider.Civitai,
            CivitaiType:      "Checkpoint",
            CivitaiBaseModel: "Pony"),

        new ModelPick(
            Id:               "image-illustrious",
            Title:            "Image — Illustrious (anime)",
            Hint:             "Novější rodina pro anime/manga styl. Často kvalitnější než starší SD 1.5 anime modely.",
            Kind:             PickKind.Image,
            Provider:         PickProvider.Civitai,
            CivitaiType:      "Checkpoint",
            CivitaiBaseModel: "Illustrious"),

        new ModelPick(
            Id:               "image-sd15",
            Title:            "Image — SD 1.5 (lite)",
            Hint:             "Lehčí (2-4 GB). Rychlé, nenáročné na VRAM. Hodí se pro starší GPU a experimenty.",
            Kind:             PickKind.Image,
            Provider:         PickProvider.Civitai,
            CivitaiType:      "Checkpoint",
            CivitaiBaseModel: "SD 1.5"),

        new ModelPick(
            Id:               "image-flux",
            Title:            "Image — FLUX (GGUF + safetensors)",
            Hint:             "FLUX.1 Schnell / Dev kvantizace. Pro generaci na střední VRAM (8-12 GB).",
            Kind:             PickKind.Image,
            Provider:         PickProvider.HuggingFace,
            Query:            "flux gguf",
            HfFilter:         "gguf"),

        // ── LoRA ────────────────────────────────────────────────────────────────
        new ModelPick(
            Id:               "lora-sdxl-styles",
            Title:            "LoRA — SDXL styly",
            Hint:             "Top SDXL LoRA pro stylové úpravy (cinematic, painted, sketch, …). Aplikuj k checkpointu.",
            Kind:             PickKind.Lora,
            Provider:         PickProvider.Civitai,
            CivitaiType:      "LORA",
            CivitaiBaseModel: "SDXL 1.0"),

        new ModelPick(
            Id:               "lora-pony",
            Title:            "LoRA — Pony",
            Hint:             "LoRA kompatibilní s Pony Diffusion checkpointy.",
            Kind:             PickKind.Lora,
            Provider:         PickProvider.Civitai,
            CivitaiType:      "LORA",
            CivitaiBaseModel: "Pony"),
    };

    // ── FetchPickAsync ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DiscoveredModel>> FetchPickAsync(
        ModelPick         pick,
        bool              bypassCache = false,
        CancellationToken ct          = default)
    {
        // Cache hit — pokud nejsme starší než TTL a uživatel nesilil refresh.
        if (!bypassCache &&
            _cache.TryGetValue(pick.Id, out var entry) &&
            DateTime.UtcNow - entry.FetchedAt < CacheTtl)
        {
            return entry.Models;
        }

        IReadOnlyList<DiscoveredModel> models;
        try
        {
            models = pick.Provider switch
            {
                PickProvider.HuggingFace => await FetchPickFromHfAsync(pick, ct),
                PickProvider.Civitai     => await FetchPickFromCivitaiAsync(pick, ct),
                _                         => Array.Empty<DiscoveredModel>()
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Discovery: pick '{Id}' selhal", pick.Id);
            models = Array.Empty<DiscoveredModel>();
        }

        // Cache jen úspěšné výsledky — prázdná odpověď typicky znamená chybu API
        // nebo špatně zformulovaný dotaz. Bez tohoto by uživatel viděl prázdnou
        // sekci 24 h i po opravě network/tokenu, dokud nepoužije „Obnovit".
        if (models.Count > 0)
            _cache[pick.Id] = (DateTime.UtcNow, models);

        return models;
    }

    private async Task<IReadOnlyList<DiscoveredModel>> FetchPickFromHfAsync(
        ModelPick pick, CancellationToken ct)
    {
        var infos = await _hf.SearchModelsAsync(
            query:     pick.Query,
            filter:    pick.HfFilter,
            task:      pick.HfTask,
            sort:      "downloads",
            direction: -1,
            limit:     pick.Limit,
            ct:        ct);

        // HF model nemá download URL přímo — obvykle chceme jeden ze souborů
        // v repu. Pro discovery view stačí ukázat repo s odkazem na page; až
        // uživatel klikne „Stáhnout", vybere konkrétní soubor (pickne nejmenší
        // GGUF kvantizaci, nebo otevře sub-list).
        var list = new List<DiscoveredModel>(infos.Count);
        foreach (var info in infos)
        {
            // Author/Name = split repoId po prvním '/'
            var slash = info.Id.IndexOf('/');
            var author = slash > 0 ? info.Id[..slash] : "";
            var name   = slash > 0 ? info.Id[(slash + 1)..] : info.Id;

            list.Add(new DiscoveredModel(
                Provider:    "HuggingFace",
                ProviderRef: info.Id,
                Name:        name,
                Author:      author,
                Description: "",                       // doplníme později on-demand v detailu
                FileName:    "",                       // doplní se až když user vybere soubor
                DownloadUrl: "",                       // dtto
                ModelPageUrl: _hf.BuildModelPageUrl(info.Id),
                SizeBytes:   0,
                Downloads:   info.Downloads,
                Rating:      0,
                Nsfw:        false,
                ThumbnailUrl: null,
                BaseModel:   GuessHfBaseModel(info.Id, pick),
                FileFormat:  pick.HfFilter == "gguf" ? "GGUF" : null));
        }
        return list;
    }

    private async Task<IReadOnlyList<DiscoveredModel>> FetchPickFromCivitaiAsync(
        ModelPick pick, CancellationToken ct)
    {
        var civType = pick.CivitaiType switch
        {
            "Checkpoint" => CivitaiType.Checkpoint,
            "LORA"       => CivitaiType.LORA,
            "LoCon"      => CivitaiType.LoCon,
            _            => CivitaiType.Any
        };

        var infos = await _civ.SearchAsync(
            query:     pick.Query,
            type:      civType,
            baseModel: pick.CivitaiBaseModel,
            sort:      CivitaiSort.MostDownloaded,
            limit:     pick.Limit,
            nsfw:      false,                          // Doporučené sekce default safe
            ct:        ct);

        var list = new List<DiscoveredModel>(infos.Count);
        foreach (var info in infos)
        {
            // Vybereme primární soubor první verze. Civitai řadí versions od
            // nejnovější (publishedAt desc) — autorizovaný download URL přiloží
            // CivitaiClient.BuildAuthorizedDownloadUrl.
            var firstVersion = info.Versions.FirstOrDefault();
            var primaryFile  = firstVersion?.Files.FirstOrDefault(f => f.Primary)
                            ?? firstVersion?.Files.FirstOrDefault();

            if (firstVersion is null || primaryFile is null)
                continue;   // Civitai občas vrátí model bez verzí — přeskočit

            list.Add(new DiscoveredModel(
                Provider:    "Civitai",
                ProviderRef: info.Id.ToString(),
                Name:        info.Name,
                Author:      info.Creator,
                Description: StripHtml(info.Description, 280),
                FileName:    primaryFile.Name,
                DownloadUrl: _civ.BuildAuthorizedDownloadUrl(primaryFile.DownloadUrl),
                ModelPageUrl: _civ.BuildModelPageUrl(info.Id),
                SizeBytes:   primaryFile.SizeKb * 1024L,
                Downloads:   info.DownloadCount,
                Rating:      info.Rating,
                Nsfw:        info.Nsfw,
                ThumbnailUrl: info.ThumbnailUrl,
                BaseModel:   firstVersion.BaseModel,
                FileFormat:  primaryFile.Format,
                Sha256:      primaryFile.Sha256));
        }
        return list;
    }

    // ── SearchAsync (free-form pro „Hledat" tab) ──────────────────────────────

    public async Task<IReadOnlyList<DiscoveredModel>> SearchAsync(
        PickProvider      provider,
        string            query,
        PickKind?         kind        = null,
        bool              includeNsfw = false,
        int               limit       = 30,
        CancellationToken ct          = default)
    {
        if (provider == PickProvider.HuggingFace)
        {
            // Pro Chat preferujeme GGUF tag; pipeline_tag záměrně neposíláme,
            // protože GGUF repos ho mají často nesetovaný a vyfiltrovalo by hodně
            // platných výsledků (viz Picks).
            var filter = kind == PickKind.Chat ? "gguf" : null;
            var infos  = await _hf.SearchModelsAsync(query, filter, null, "downloads", -1, limit, ct);

            return infos.Select(i =>
            {
                var slash = i.Id.IndexOf('/');
                return new DiscoveredModel(
                    "HuggingFace", i.Id,
                    slash > 0 ? i.Id[(slash + 1)..] : i.Id,
                    slash > 0 ? i.Id[..slash] : "",
                    "", "", "",
                    _hf.BuildModelPageUrl(i.Id),
                    0, i.Downloads, 0, false, null,
                    null, kind == PickKind.Chat ? "GGUF" : null);
            }).ToList();
        }
        else
        {
            // Civitai: filter podle Kind → typ
            var civType = kind switch
            {
                PickKind.Image => CivitaiType.Checkpoint,
                PickKind.Lora  => CivitaiType.LORA,
                _              => CivitaiType.Any
            };

            var infos = await _civ.SearchAsync(
                query:     query,
                type:      civType,
                baseModel: null,
                sort:      CivitaiSort.MostDownloaded,
                limit:     limit,
                nsfw:      includeNsfw,
                ct:        ct);

            var list = new List<DiscoveredModel>(infos.Count);
            foreach (var info in infos)
            {
                var firstVersion = info.Versions.FirstOrDefault();
                var primaryFile  = firstVersion?.Files.FirstOrDefault(f => f.Primary)
                                ?? firstVersion?.Files.FirstOrDefault();
                if (firstVersion is null || primaryFile is null) continue;

                list.Add(new DiscoveredModel(
                    "Civitai", info.Id.ToString(),
                    info.Name, info.Creator,
                    StripHtml(info.Description, 280),
                    primaryFile.Name,
                    _civ.BuildAuthorizedDownloadUrl(primaryFile.DownloadUrl),
                    _civ.BuildModelPageUrl(info.Id),
                    primaryFile.SizeKb * 1024L,
                    info.DownloadCount, info.Rating, info.Nsfw, info.ThumbnailUrl,
                    firstVersion.BaseModel, primaryFile.Format,
                    Sha256: primaryFile.Sha256));
            }
            return list;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Civitai má baseModel přímo, HF ne — ale dá se odhadnout z repoId / pick názvu.
    /// </summary>
    private static string? GuessHfBaseModel(string repoId, ModelPick pick)
    {
        var lower = repoId.ToLowerInvariant();
        if (lower.Contains("flux"))    return "FLUX.1";
        if (lower.Contains("sdxl"))    return "SDXL";
        if (lower.Contains("sd-1-5") || lower.Contains("sd15") || lower.Contains("sd1.5")) return "SD 1.5";
        if (lower.Contains("llama"))   return "Llama";
        if (lower.Contains("qwen"))    return "Qwen";
        if (lower.Contains("mistral")) return "Mistral";
        if (lower.Contains("gemma"))   return "Gemma";
        return null;
    }

    /// <summary>
    /// Civitai vrací popisky jako HTML. Pro UI chceme plain text — zbavíme se tagů
    /// a odřízneme na rozumnou délku. Není to dokonalé (nesleduje &amp;entity, etc.),
    /// ale pro 1-2 věty popisku v sekci stačí.
    /// </summary>
    private static string StripHtml(string html, int maxChars)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var sb = new System.Text.StringBuilder(html.Length);
        bool insideTag = false;
        foreach (var ch in html)
        {
            if (ch == '<')      insideTag = true;
            else if (ch == '>') insideTag = false;
            else if (!insideTag) sb.Append(ch);
        }
        var text = sb.ToString()
                     .Replace("&nbsp;", " ")
                     .Replace("&amp;", "&")
                     .Replace("&lt;", "<")
                     .Replace("&gt;", ">")
                     .Replace("&quot;", "\"")
                     .Trim();

        // Zploštíme whitespace (řádky → mezery)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }
}
