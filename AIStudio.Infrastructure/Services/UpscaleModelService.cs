using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Stáhne a spravuje ESRGAN upscale model (RealESRGAN_x4plus.pth, ~64 MB) pro
/// „hires fix + upscale" generování. Model jde do <c>{modelsDir}/upscale_models/</c>,
/// kde ho ComfyUI <c>UpscaleModelLoader</c> vidí přes extra_model_paths.yaml.
/// Zdroj je veřejný GitHub release (xinntao/Real-ESRGAN) — bez tokenu.
/// </summary>
public sealed class UpscaleModelService : IUpscaleModelService
{
    private readonly IDownloadService _downloader;

    private const string ModelFile   = "RealESRGAN_x4plus.pth";
    private const string ModelSubdir = "upscale_models";
    private const string ModelUrl =
        "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth";

    private volatile bool   _isDownloading;
    private volatile string _statusLine = string.Empty;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public UpscaleModelService(IDownloadService downloader) => _downloader = downloader;

    public string ModelFileName      => ModelFile;
    public bool   IsDownloading      => _isDownloading;
    public string DownloadStatusLine => _statusLine;

    public bool IsAvailable(string modelsDir)
    {
        if (string.IsNullOrWhiteSpace(modelsDir)) return false;
        var inRoot = Path.Combine(modelsDir, ModelFile);
        var inSub  = Path.Combine(modelsDir, ModelSubdir, ModelFile);
        return File.Exists(inRoot) || File.Exists(inSub);
    }

    public async Task EnsureAsync(string modelsDir,
        IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelsDir)) return;
        if (IsAvailable(modelsDir)) return;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (IsAvailable(modelsDir)) return;   // mezitím dostáhl jiný caller

            _isDownloading = true;
            var subDir  = Path.Combine(modelsDir, ModelSubdir);
            Directory.CreateDirectory(subDir);
            var dstPath = Path.Combine(subDir, ModelFile);
            var tmpPath = dstPath + ".tmp";

            _statusLine = "Stahuji upscale model (RealESRGAN, ~64 MB)…";
            Log.Information("UpscaleModelService: stahuji {File} → {Dir}", ModelFile, subDir);

            var wrapped = new Progress<DownloadProgressInfo>(info =>
            {
                progress?.Report(info);
                var pct = info.Total > 0 ? (int)(100 * info.Downloaded / info.Total) : 0;
                _statusLine = info.Total > 0
                    ? $"Stahuji upscale model {pct} %"
                    : "Stahuji upscale model…";
            });

            try
            {
                await _downloader.DownloadFileAsync(ModelUrl, tmpPath, wrapped, apiToken: null, ct: ct);
                if (File.Exists(dstPath)) File.Delete(dstPath);
                File.Move(tmpPath, dstPath);
                Log.Information("UpscaleModelService: {File} stažen → {Path}", ModelFile, dstPath);
            }
            catch (OperationCanceledException)
            {
                TryDeleteTmp(tmpPath);
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "UpscaleModelService: stahování {File} selhalo", ModelFile);
                TryDeleteTmp(tmpPath);
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
        catch (Exception ex) { Log.Warning(ex, "UpscaleModelService: nelze smazat {Path}", path); }
    }
}
