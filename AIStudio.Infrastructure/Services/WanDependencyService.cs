using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Stahuje Wan 2.1 video závislosti (text encoder, VAE, CLIP-Vision, diffusion model)
/// na pozadí. Vzor a chování shodné s <see cref="FluxDependencyService"/> — jen je
/// parametrizováno konkrétním <see cref="WanVideoModel"/> (každý model potřebuje jinou
/// sadu souborů). Soubory míří do ComfyUI podsložek (text_encoders/, vae/, clip_vision/,
/// diffusion_models/), kde je ComfyUI vidí přes extra_model_paths.yaml.
/// </summary>
public sealed class WanDependencyService : IWanDependencyService
{
    private readonly IDownloadService _downloader;

    private volatile bool   _isDownloading;
    private volatile string _downloadingFileName = string.Empty;
    private long            _downloadedBytes;   // Interlocked
    private long            _totalBytes;        // Interlocked
    private int             _runningFlag;       // 0 = idle, 1 = running

    public bool   IsDownloading       => _isDownloading;
    public string DownloadingFileName => _downloadingFileName;
    public long   DownloadedBytes     => Interlocked.Read(ref _downloadedBytes);
    public long   TotalBytes          => Interlocked.Read(ref _totalBytes);

    public string DownloadStatusLine
    {
        get
        {
            if (!_isDownloading) return string.Empty;
            var total = TotalBytes;
            var done  = DownloadedBytes;
            var file  = _downloadingFileName;
            if (total <= 0) return $"Stahuji Wan závislost: {file}…";
            return $"Stahuji {file} ({done / 1_048_576} / {total / 1_048_576} MB)";
        }
    }

    public WanDependencyService(IDownloadService downloader) => _downloader = downloader;

    // ── Dotazy ─────────────────────────────────────────────────────────────────

    public bool AreDependenciesPresent(string modelsDir, WanVideoModel model)
    {
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir)) return false;
        return WanModels.RequiredFiles(model).All(f => IsPresent(modelsDir, f));
    }

    public IReadOnlyList<string> FindMissing(string modelsDir, WanVideoModel model)
    {
        var files = WanModels.RequiredFiles(model);
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
            return files.Select(f => f.Label).ToList();
        return files.Where(f => !IsPresent(modelsDir, f)).Select(f => f.Label).ToList();
    }

    // ── Stahování ──────────────────────────────────────────────────────────────

    public async Task EnsureAsync(string modelsDir, WanVideoModel model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelsDir)) return;
        if (AreDependenciesPresent(modelsDir, model)) return;

        if (Interlocked.CompareExchange(ref _runningFlag, 1, 0) != 0)
        {
            Log.Debug("WanDependencyService.EnsureAsync: stahování již probíhá, přeskakuji");
            return;
        }

        _isDownloading = true;
        try
        {
            await DownloadAllAsync(modelsDir, model, ct);
        }
        finally
        {
            _isDownloading       = false;
            _downloadingFileName = string.Empty;
            Interlocked.Exchange(ref _downloadedBytes, 0);
            Interlocked.Exchange(ref _totalBytes, 0);
            Interlocked.Exchange(ref _runningFlag, 0);
        }
    }

    private async Task DownloadAllAsync(string modelsDir, WanVideoModel model, CancellationToken ct)
    {
        foreach (var dep in WanModels.RequiredFiles(model))
        {
            ct.ThrowIfCancellationRequested();

            if (IsPresent(modelsDir, dep))
            {
                Log.Debug("WanDependencyService: {File} již přítomen, přeskakuji", dep.FileName);
                continue;
            }

            var subDir  = Path.Combine(modelsDir, dep.Subdir);
            var tmpPath = Path.Combine(subDir, dep.FileName + ".tmp");
            var dstPath = Path.Combine(subDir, dep.FileName);
            Directory.CreateDirectory(subDir);

            Log.Information("WanDependencyService: stahuji {File} → {Dir}", dep.FileName, subDir);
            _downloadingFileName = dep.FileName;
            Interlocked.Exchange(ref _downloadedBytes, 0);
            Interlocked.Exchange(ref _totalBytes, 0);

            var progress = new Progress<DownloadProgressInfo>(info =>
            {
                Interlocked.Exchange(ref _downloadedBytes, info.Downloaded);
                Interlocked.Exchange(ref _totalBytes,      info.Total);
            });

            try
            {
                await _downloader.DownloadFileAsync(dep.Url, tmpPath, progress, apiToken: null, ct);
                if (File.Exists(dstPath)) File.Delete(dstPath);
                File.Move(tmpPath, dstPath);
                Log.Information("WanDependencyService: {File} stažen → {Path}", dep.FileName, dstPath);
            }
            catch (OperationCanceledException)
            {
                Log.Information("WanDependencyService: stahování {File} zrušeno", dep.FileName);
                TryDeleteTmp(tmpPath);
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WanDependencyService: stahování {File} selhalo", dep.FileName);
                TryDeleteTmp(tmpPath);
                // Pokračujeme dalšími — chyba v jednom nesmí zastavit ostatní.
            }
        }
    }

    /// <summary>Soubor je přítomen v rootu nebo v očekávané podsložce (obojí vidí ComfyUI).</summary>
    private static bool IsPresent(string modelsDir, WanDownloadFile dep)
    {
        var inRoot = Path.Combine(modelsDir, dep.FileName);
        var inSub  = Path.Combine(modelsDir, dep.Subdir, dep.FileName);
        return File.Exists(inRoot) || File.Exists(inSub);
    }

    private static void TryDeleteTmp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warning(ex, "WanDependencyService: nelze smazat {Path}", path); }
    }
}
