using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Zajišťuje FLUX.1 Kontext [dev] — model pro instrukční editaci obrázku
/// (přilož originál + textová instrukce → upravený obrázek se zachováním scény).
/// Nejbližší lokální ekvivalent editace obrázku v ChatGPT.
///
/// <para>Kontext potřebuje UNET (<c>flux1-dev-kontext_fp8_scaled.safetensors</c>,
/// ~12 GB) + sdílené FLUX závislosti (clip_l, t5xxl, ae) — ty samé, co používá
/// FLUX GGUF. Tahle služba kontroluje přítomnost a na vyžádání vše stáhne
/// (idempotentně), aby uživatel nemusel nic řešit ručně.</para>
/// </summary>
public interface IKontextService
{
    /// <summary>Název UNET souboru, který očekává ComfyUI <c>UNETLoader</c>.</summary>
    string UnetFileName { get; }

    /// <summary>
    /// True pokud je Kontext připravený generovat — UNET i všechny FLUX
    /// závislosti jsou v <paramref name="modelsDir"/> přítomné.
    /// </summary>
    bool IsAvailable(string modelsDir);

    /// <summary>Probíhá stahování Kontext modelu nebo jeho závislostí.</summary>
    bool IsDownloading { get; }

    /// <summary>Lidský status řádek pro UI během stahování (např. „Stahuji FLUX Kontext 45 %…").</summary>
    string DownloadStatusLine { get; }

    /// <summary>
    /// Zajistí přítomnost Kontext modelu i FLUX závislostí. Idempotentní —
    /// pokud je vše stažené, hned se vrátí. Chybějící soubory stáhne (UNET ~12 GB
    /// + případně clip/t5/vae). Progress jde přes <paramref name="progress"/>.
    /// </summary>
    Task EnsureAsync(string modelsDir, string? hfToken,
                     IProgress<DownloadProgressInfo>? progress, CancellationToken ct);
}
