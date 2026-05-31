using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Zajišťuje FLUX.1 Kontext [dev] model pro instrukční editaci obrázku.
/// Kontroluje přítomnost a na vyžádání stáhne UNET (~12 GB) + sdílené FLUX
/// závislosti (clip_l, t5, ae) přes <see cref="IFluxDependencyService"/>.
///
/// <para>UNET jde do <c>{modelsDir}/unet/</c> — ComfyUI ho vidí přes
/// extra_model_paths.yaml (mapuje <c>unet:</c> na root i <c>unet/</c>). Zdroj je
/// veřejný Comfy-Org fp8 repackage, takže stahování nepotřebuje HF token.</para>
/// </summary>
public sealed class KontextDependencyService : IKontextService
{
    private readonly IDownloadService       _downloader;
    private readonly IFluxDependencyService _fluxDeps;

    private const string UnetFile   = "flux1-dev-kontext_fp8_scaled.safetensors";
    private const string UnetSubdir = "unet";
    private const string UnetUrl =
        "https://huggingface.co/Comfy-Org/flux1-kontext-dev_ComfyUI/resolve/main/" +
        "split_files/diffusion_models/flux1-dev-kontext_fp8_scaled.safetensors";

    private volatile bool   _isDownloading;
    private volatile string _statusLine = string.Empty;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public KontextDependencyService(IDownloadService downloader, IFluxDependencyService fluxDeps)
    {
        _downloader = downloader;
        _fluxDeps   = fluxDeps;
    }

    public string UnetFileName       => UnetFile;
    public bool   IsDownloading      => _isDownloading;
    public string DownloadStatusLine => _statusLine;

    public bool IsAvailable(string modelsDir)
    {
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
            return false;
        return IsUnetPresent(modelsDir) && _fluxDeps.AreDependenciesPresent(modelsDir);
    }

    /// <summary>UNET může být v root Models nebo v <c>unet/</c> — ComfyUI vidí obojí.</summary>
    private static bool IsUnetPresent(string modelsDir)
    {
        var inRoot = Path.Combine(modelsDir, UnetFile);
        var inSub  = Path.Combine(modelsDir, UnetSubdir, UnetFile);
        return File.Exists(inRoot) || File.Exists(inSub);
    }

    public async Task EnsureAsync(string modelsDir, string? hfToken,
        IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelsDir)) return;
        if (IsAvailable(modelsDir)) return;

        // Souběžné volání NEpřeskakujeme — POČKÁME na probíhající stahování a pak
        // (až je model k dispozici) se vrátíme. Dřív druhý edit guard přeskočil,
        // IsAvailable zůstal false a generování spadlo na destruktivní fallback
        // img2img, který ignoruje vstupní fotku (vygeneroval nesouvisející obrázek).
        await _downloadLock.WaitAsync(ct);
        try
        {
            if (IsAvailable(modelsDir)) return;   // mezitím dostáhl jiný caller

            _isDownloading = true;
            // 1) Sdílené FLUX závislosti (clip_l, t5, ae) — reuse existující služby
            if (!_fluxDeps.AreDependenciesPresent(modelsDir))
            {
                _statusLine = "Stahuji FLUX závislosti (CLIP, T5, VAE)…";
                Log.Information("KontextDependencyService: stahuji FLUX závislosti");
                await _fluxDeps.EnsureAsync(modelsDir, hfToken, ct);
            }

            // 2) Kontext UNET (~12 GB) — Comfy-Org fp8 repackage, veřejné
            if (!IsUnetPresent(modelsDir))
            {
                var subDir  = Path.Combine(modelsDir, UnetSubdir);
                Directory.CreateDirectory(subDir);
                var dstPath = Path.Combine(subDir, UnetFile);
                var tmpPath = dstPath + ".tmp";

                _statusLine = "Stahuji FLUX Kontext model…";
                Log.Information("KontextDependencyService: stahuji {File} (~12 GB) → {Dir}", UnetFile, subDir);

                var wrapped = new Progress<DownloadProgressInfo>(info =>
                {
                    progress?.Report(info);
                    var pct = info.Total > 0 ? (int)(100 * info.Downloaded / info.Total) : 0;
                    _statusLine = info.Total > 0
                        ? $"Stahuji FLUX Kontext {pct} % ({info.Downloaded / 1_048_576} / {info.Total / 1_048_576} MB)"
                        : "Stahuji FLUX Kontext model…";
                });

                try
                {
                    await _downloader.DownloadFileAsync(UnetUrl, tmpPath, wrapped, apiToken: hfToken, ct: ct);

                    if (File.Exists(dstPath)) File.Delete(dstPath);
                    File.Move(tmpPath, dstPath);
                    Log.Information("KontextDependencyService: {File} stažen → {Path}", UnetFile, dstPath);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("KontextDependencyService: stahování {File} zrušeno", UnetFile);
                    TryDeleteTmp(tmpPath);
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "KontextDependencyService: stahování {File} selhalo", UnetFile);
                    TryDeleteTmp(tmpPath);
                }
            }
        }
        finally
        {
            _isDownloading = false;
            _statusLine    = string.Empty;
            _downloadLock.Release();
        }
    }

    private static void TryDeleteTmp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warning(ex, "KontextDependencyService: nelze smazat {Path}", path); }
    }
}
