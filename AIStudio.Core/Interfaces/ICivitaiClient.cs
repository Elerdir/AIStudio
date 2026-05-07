using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Civitai search filtry. Hodnoty se posílají rovnou do query parametrů
/// API, takže odpovídají tomu, co Civitai REST přijímá.
/// </summary>
public enum CivitaiType
{
    /// <summary>Checkpoint = celý SD/SDXL/FLUX checkpoint.</summary>
    Checkpoint,
    LORA,
    LoCon,
    TextualInversion,
    Hypernetwork,
    AestheticGradient,
    Controlnet,
    Poses,
    /// <summary>Neptat na typ — vrátí všechno.</summary>
    Any
}

/// <summary>
/// Pořadí výsledků. Civitai akceptuje "Highest Rated", "Most Downloaded", "Newest"
/// (s mezerami v hodnotě, je třeba escapovat).
/// </summary>
public enum CivitaiSort { HighestRated, MostDownloaded, Newest }

/// <summary>
/// Klient pro Civitai REST API (<c>https://civitai.com/api/v1</c>).
/// Token z <see cref="Models.AppSettings.CivitaiApiKey"/> jde do Bearer hlavičky;
/// bez tokenu fungují jen veřejné modely a download je rate-limited.
/// </summary>
public interface ICivitaiClient
{
    /// <summary>
    /// Hledání modelů. Pokud <paramref name="query"/> je prázdné, vrátí
    /// top-N modelů podle zvoleného <paramref name="sort"/>.
    /// </summary>
    /// <param name="baseModel">Filtruje na konkrétní base (<c>"SDXL 1.0"</c>, <c>"SD 1.5"</c>, <c>"Pony"</c>, <c>"Flux.1 D"</c>).</param>
    /// <param name="nsfw">false = vyloučí NSFW. Civitai default je vrátit i NSFW (pokud má uživatel v tokenu povoleno).</param>
    Task<IReadOnlyList<CivitaiModelInfo>> SearchAsync(
        string?           query      = null,
        CivitaiType       type       = CivitaiType.Any,
        string?           baseModel  = null,
        CivitaiSort       sort       = CivitaiSort.MostDownloaded,
        int               limit      = 20,
        bool              nsfw       = false,
        CancellationToken ct         = default);

    /// <summary>
    /// Vrátí URL pro stažení konkrétního souboru. Civitai přidá token jako
    /// query parameter <c>?token=…</c>, takže bez tokenu by stahování pro
    /// gated soubory nefungovalo. Když token chybí, link je „naked" a
    /// Civitai si vyžádá web login.
    /// </summary>
    string BuildAuthorizedDownloadUrl(string downloadUrl);

    /// <summary>URL na model page pro tlačítko „Otevřít v prohlížeči".</summary>
    string BuildModelPageUrl(long modelId);
}
