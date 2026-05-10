using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Stahuje FLUX textové encodery (CLIP-L, T5-XXL fp8) a VAE (ae) na pozadí.
///
/// Proč tyto soubory:
///   FLUX GGUF checkpointy obsahují jen difúzní model (UNet/transformer).
///   Aby fungovalo generování, ComfyUI potřebuje i textové encodery (CLIP-L + T5-XXL)
///   a VAE pro enkódování/dekódování latentního prostoru. Bez nich generování padne
///   s "clip input is invalid: None".
///
/// Umístění:
///   {modelsDir}/clip/clip_l.safetensors              (~246 MB, veřejné)
///   {modelsDir}/clip/t5xxl_fp8_e4m3fn.safetensors    (~4.9 GB, veřejné)
///   {modelsDir}/vae/ae.safetensors                   (~335 MB, vyžaduje HF token)
///
/// ComfyUI tyto cesty vidí přes extra_model_paths.yaml, který AIStudio generuje
/// při startu ComfyUI.
/// </summary>
public sealed class FluxDependencyService : IFluxDependencyService
{
    private readonly IDownloadService _downloader;

    // Definice závislostí — pořadí = pořadí stahování (nejdřív menší soubory)
    private static readonly DepInfo[] Deps =
    [
        new("clip_l.safetensors",
            "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/clip_l.safetensors",
            Subdir: "clip",
            RequiresToken: false),

        new("ae.safetensors",
            "https://huggingface.co/black-forest-labs/FLUX.1-schnell/resolve/main/ae.safetensors",
            Subdir: "vae",
            RequiresToken: true),   // FLUX.1-schnell je gated — potřebuje HF token

        new("t5xxl_fp8_e4m3fn.safetensors",
            "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/t5xxl_fp8_e4m3fn.safetensors",
            Subdir: "clip",
            RequiresToken: false),  // stahujeme jako poslední — největší (~4.9 GB)
    ];

    // ── Stav probíhajícího stahování (volatile pro thread-safe read bez lock) ──

    private volatile bool   _isDownloading;
    private volatile string _downloadingFileName = string.Empty;
    private long            _downloadedBytes;   // Interlocked
    private long            _totalBytes;        // Interlocked

    // Brání souběžnému dvojitému spuštění
    private int _runningFlag; // 0 = idle, 1 = running (Interlocked.CompareExchange)

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
            if (total <= 0) return $"Stahuji FLUX závislost: {file}…";
            return $"Stahuji {file} ({done / 1_048_576} / {total / 1_048_576} MB)";
        }
    }

    public FluxDependencyService(IDownloadService downloader)
    {
        _downloader = downloader;
    }

    // ── Veřejné dotazy ────────────────────────────────────────────────────────

    public bool AreDependenciesPresent(string modelsDir)
    {
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
            return false;

        foreach (var dep in Deps)
        {
            if (!IsPresent(modelsDir, dep))
                return false;
        }
        return true;
    }

    public bool HasGgufModels(string modelsDir)
    {
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
            return false;

        try
        {
            return Directory
                .EnumerateFiles(modelsDir, "*.gguf", SearchOption.AllDirectories)
                .Any(f =>
                {
                    var fn = Path.GetFileName(f);
                    return fn.StartsWith("flux", StringComparison.OrdinalIgnoreCase)
                        || fn.StartsWith("sd",   StringComparison.OrdinalIgnoreCase);
                });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "FluxDependencyService: nelze skenovat {Dir}", modelsDir);
            return false;
        }
    }

    // ── Stahování ─────────────────────────────────────────────────────────────

    public async Task EnsureAsync(string modelsDir, string? hfToken = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelsDir)) return;
        if (AreDependenciesPresent(modelsDir))    return;

        // Pokud stahování už běží, ignorujeme druhé volání
        if (Interlocked.CompareExchange(ref _runningFlag, 1, 0) != 0)
        {
            Log.Debug("FluxDependencyService.EnsureAsync: stahování již probíhá, přeskakuji");
            return;
        }

        _isDownloading = true;
        try
        {
            await DownloadAllAsync(modelsDir, hfToken, ct);
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

    // ── Interní logika ────────────────────────────────────────────────────────

    private async Task DownloadAllAsync(string modelsDir, string? hfToken, CancellationToken ct)
    {
        foreach (var dep in Deps)
        {
            ct.ThrowIfCancellationRequested();

            if (IsPresent(modelsDir, dep))
            {
                Log.Debug("FluxDependencyService: {File} již přítomen, přeskakuji", dep.FileName);
                continue;
            }

            // ae.safetensors potřebuje HF token — pokud ho uživatel nemá nastavený,
            // přeskočíme s informativním logem (pre-flight check v generátoru to vysvětlí).
            if (dep.RequiresToken && string.IsNullOrWhiteSpace(hfToken))
            {
                Log.Warning(
                    "FluxDependencyService: {File} vyžaduje HuggingFace token — " +
                    "nastav ho v Nastavení → Stahování modelů → HuggingFace Token. " +
                    "Mezitím přeskakuji.", dep.FileName);
                continue;
            }

            var subDir  = Path.Combine(modelsDir, dep.Subdir);
            var tmpPath = Path.Combine(subDir, dep.FileName + ".tmp");
            var dstPath = Path.Combine(subDir, dep.FileName);

            Directory.CreateDirectory(subDir);

            Log.Information("FluxDependencyService: zahajuji stahování {File} → {Dir}",
                            dep.FileName, subDir);

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
                await _downloader.DownloadFileAsync(
                    dep.Url, tmpPath, progress,
                    apiToken: dep.RequiresToken ? hfToken : null,
                    ct);

                // Přesuneme .tmp → finální jméno — atomičtější než psát přímo
                if (File.Exists(dstPath)) File.Delete(dstPath);
                File.Move(tmpPath, dstPath);

                Log.Information("FluxDependencyService: {File} stažen → {Path}",
                                dep.FileName, dstPath);
            }
            catch (OperationCanceledException)
            {
                Log.Information("FluxDependencyService: stahování {File} zrušeno", dep.FileName);
                TryDeleteTmp(tmpPath);
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "FluxDependencyService: stahování {File} selhalo", dep.FileName);
                TryDeleteTmp(tmpPath);
                // Pokračujeme dalšími soubory — chyba v jednom nesmí zastavit ostatní
            }
        }
    }

    /// <summary>
    /// Vrátí true pokud soubor existuje v root adresáři nebo v očekávané podsložce.
    /// ComfyUI hledá přes extra_model_paths.yaml v obou místech.
    /// </summary>
    private static bool IsPresent(string modelsDir, DepInfo dep)
    {
        var inRoot = Path.Combine(modelsDir, dep.FileName);
        var inSub  = Path.Combine(modelsDir, dep.Subdir, dep.FileName);
        return File.Exists(inRoot) || File.Exists(inSub);
    }

    private static void TryDeleteTmp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warning(ex, "FluxDependencyService: nelze smazat {Path}", path); }
    }

    // ── Statická helper — centralizovaná detekce dep souborů ─────────────────

    /// <summary>
    /// Vrátí true pokud je název souboru FLUX závislostí (CLIP-L, T5, VAE).
    /// Tyto soubory se nemají zobrazovat v pickeru modelů — nejsou checkpointy.
    /// </summary>
    public static bool IsFluxDep(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower.StartsWith("clip_l")
            || lower.StartsWith("t5xxl")
            || lower.StartsWith("t5-xxl")
            || lower == "ae.safetensors"
            || lower.StartsWith("ae.")
            || lower.StartsWith("flux_vae");
    }

    // ── Privátní record pro metadata závislosti ───────────────────────────────

    private sealed record DepInfo(
        string FileName,
        string Url,
        string Subdir,
        bool   RequiresToken);
}
