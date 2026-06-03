using AIStudio.Core.Services;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Zajišťuje přítomnost Wan 2.1 video závislostí (text encoder umT5, VAE, příp.
/// CLIP-Vision a samotný diffusion model) na pozadí. Modely jsou velké (jednotky až
/// desítky GB) — stahují se z veřejného repackage repa bez HF tokenu.
/// </summary>
public interface IWanDependencyService
{
    // ── Stav probíhajícího stahování ─────────────────────────────────────────
    bool   IsDownloading       { get; }
    string DownloadingFileName { get; }
    long   DownloadedBytes     { get; }
    long   TotalBytes          { get; }
    string DownloadStatusLine  { get; }

    /// <summary>True když jsou všechny soubory pro daný model přítomny.</summary>
    bool AreDependenciesPresent(string modelsDir, WanVideoModel model);

    /// <summary>Lidská jména chybějících souborů (prázdné = vše OK).</summary>
    IReadOnlyList<string> FindMissing(string modelsDir, WanVideoModel model);

    /// <summary>
    /// Stáhne chybějící soubory pro daný model. Idempotentní — co je, přeskočí;
    /// pokud stahování běží, druhé volání se vrátí ihned.
    /// </summary>
    Task EnsureAsync(string modelsDir, WanVideoModel model, CancellationToken ct = default);
}
