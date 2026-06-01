using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Zajišťuje ESRGAN upscale model (RealESRGAN_x4plus, ~64 MB) pro „hires fix +
/// upscale" cestu v generování obrázků. Model jde do <c>{modelsDir}/upscale_models/</c>
/// — ComfyUI ho vidí přes extra_model_paths.yaml (<c>upscale_models:</c>). Zdroj je
/// veřejný GitHub release, takže stahování nepotřebuje token.
/// </summary>
public interface IUpscaleModelService
{
    /// <summary>Název souboru, který očekává ComfyUI <c>UpscaleModelLoader</c>.</summary>
    string ModelFileName { get; }

    /// <summary>True pokud je upscale model v <paramref name="modelsDir"/> přítomný.</summary>
    bool IsAvailable(string modelsDir);

    /// <summary>Probíhá stahování upscale modelu.</summary>
    bool IsDownloading { get; }

    /// <summary>Lidský status řádek pro UI během stahování.</summary>
    string DownloadStatusLine { get; }

    /// <summary>
    /// Zajistí přítomnost upscale modelu. Idempotentní — pokud existuje, hned se
    /// vrátí; jinak ho stáhne (~64 MB). Progress jde přes <paramref name="progress"/>.
    /// </summary>
    Task EnsureAsync(string modelsDir,
                     IProgress<DownloadProgressInfo>? progress, CancellationToken ct);
}
