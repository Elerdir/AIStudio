namespace AIStudio.Core.Interfaces;

/// <summary>
/// Zajišťuje přítomnost FLUX textových encoderů (CLIP-L, T5-XXL) a VAE na pozadí.
/// Tyto soubory jsou povinné pro FLUX GGUF workflow, ale nejsou samy o sobě
/// modely pro generování — nesmí se zobrazovat v pickeru checkpointů.
/// </summary>
public interface IFluxDependencyService
{
    // ── Stav probíhajícího stahování ─────────────────────────────────────────

    bool   IsDownloading        { get; }
    string DownloadingFileName  { get; }   // aktuálně stahovaný soubor
    long   DownloadedBytes      { get; }
    long   TotalBytes           { get; }

    /// <summary>
    /// True pokud stahování právě probíhá — pro informační hlášku v UI
    /// ("závislosti se stahují na pozadí").
    /// </summary>
    string DownloadStatusLine { get; }

    // ── Dotazy na stav závislostí ─────────────────────────────────────────────

    /// <summary>
    /// Vrátí true pokud jsou všechny tři FLUX závislosti přítomny v daném adresáři
    /// (prohledáme root i podsložky clip/ a vae/).
    /// </summary>
    bool AreDependenciesPresent(string modelsDir);

    /// <summary>
    /// Vrátí lidská jména chybějících FLUX závislostí (např. <c>["CLIP-L", "VAE"]</c>).
    /// Prázdný seznam = všechno je v pořádku. Slouží pro pre-flight check
    /// v generování — UI uživateli ukáže konkrétní seznam toho, co je třeba stáhnout.
    /// </summary>
    IReadOnlyList<string> FindMissing(string modelsDir);

    /// <summary>
    /// Vrátí true pokud modelsDir obsahuje alespoň jeden .gguf soubor —
    /// to je spolehlivý signál, že uživatel FLUX GGUF model používá.
    /// </summary>
    bool HasGgufModels(string modelsDir);

    // ── Stahování ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Zkontroluje a případně stáhne chybějící FLUX závislosti.
    /// Metoda je idempotentní — pokud jsou závislosti přítomny, nedělá nic.
    /// Pokud stahování právě probíhá, druhé volání se vrátí ihned.
    /// </summary>
    Task EnsureAsync(string modelsDir, string? hfToken = null, CancellationToken ct = default);
}
