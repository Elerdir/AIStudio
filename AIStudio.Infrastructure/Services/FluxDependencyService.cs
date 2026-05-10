using System.Net;
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
///   {modelsDir}/vae/ae.safetensors                   (~335 MB, veřejné přes FLUX.1-dev)
///
/// ComfyUI tyto cesty vidí přes extra_model_paths.yaml, který AIStudio generuje
/// při startu ComfyUI.
/// </summary>
public sealed class FluxDependencyService : IFluxDependencyService
{
    private readonly IDownloadService _downloader;

    // Definice závislostí — pořadí = pořadí stahování (nejdřív menší soubory)
    // PublicUrl   = zkusíme vždy první (bez tokenu)
    // GatedUrl    = záloha pokud PublicUrl vrátí 401/403 — vyžaduje HF Bearer token
    private static readonly DepInfo[] Deps =
    [
        new(FileName:  "clip_l.safetensors",
            PublicUrl: "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/clip_l.safetensors",
            GatedUrl:  null,
            Subdir:    "clip"),

        new(FileName:  "ae.safetensors",
            // comfyanonymous hostuje pouze text encodery, VAE je od BFL.
            // FLUX.1-dev má VAE přístupnou přes Apache-2.0 licenci bez nutnosti
            // přijmout extra podmínky — zkusíme nejdřív bez tokenu;
            // pokud HF vrátí 401, zkusíme s tokenem (pro uživatele s HF účtem).
            PublicUrl: "https://huggingface.co/black-forest-labs/FLUX.1-dev/resolve/main/ae.safetensors",
            GatedUrl:  "https://huggingface.co/black-forest-labs/FLUX.1-schnell/resolve/main/ae.safetensors",
            Subdir:    "vae"),

        new(FileName:  "t5xxl_fp8_e4m3fn.safetensors",
            PublicUrl: "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/t5xxl_fp8_e4m3fn.safetensors",
            GatedUrl:  null,
            Subdir:    "clip"),  // stahujeme jako poslední — největší (~4.9 GB)
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
                await DownloadDepAsync(dep, tmpPath, hfToken, progress, ct);

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
    /// Stáhne jednu závislost. Strategie:
    ///   1) Zkus PublicUrl bez tokenu — funguje pro veřejné soubory i repos kde BFL
    ///      nepožaduje přijetí podmínek.
    ///   2) Pokud dostaneme 401/403 a dep má GatedUrl, zkus GatedUrl s tokenem.
    ///   3) Pokud ani to nejde (žádný token nebo GatedUrl chybí), vyhoď výjimku.
    /// </summary>
    private async Task DownloadDepAsync(
        DepInfo dep, string tmpPath, string? hfToken,
        IProgress<DownloadProgressInfo> progress, CancellationToken ct)
    {
        // Pokus č.1 — bez tokenu
        if (await TryDownloadAsync(dep.PublicUrl, tmpPath, token: null, progress, ct))
            return;

        Log.Information(
            "FluxDependencyService: {File} z veřejné URL vrátil 401/403, zkouším zálohu s tokenem",
            dep.FileName);

        // Pokus č.2 — s tokenem (jen pokud máme token A gated URL)
        var gatedUrl = dep.GatedUrl ?? dep.PublicUrl;

        if (string.IsNullOrWhiteSpace(hfToken))
        {
            throw new InvalidOperationException(
                $"Soubor {dep.FileName} vyžaduje přihlášení k HuggingFace. " +
                "Nastav HuggingFace token v Nastavení → Stahování modelů.");
        }

        // Smaž případný .tmp z prvního pokusu, aby se nestahoval zbytečný prefix
        TryDeleteTmp(tmpPath);
        await _downloader.DownloadFileAsync(gatedUrl, tmpPath, progress, apiToken: hfToken, ct);
    }

    /// <summary>
    /// Pokusí se stáhnout URL. Vrátí false pokud server odpoví 401 nebo 403
    /// (tj. vyžaduje autentizaci), true pokud se stahování podařilo.
    /// Ostatní HTTP chyby propaguje jako výjimku.
    /// </summary>
    private async Task<bool> TryDownloadAsync(
        string url, string tmpPath, string? token,
        IProgress<DownloadProgressInfo> progress, CancellationToken ct)
    {
        try
        {
            await _downloader.DownloadFileAsync(url, tmpPath, progress, apiToken: token, ct);
            return true;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            TryDeleteTmp(tmpPath);
            return false;
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

        // Exact matches — FLUX VAE and CLIP encoders have well-known filenames.
        // Avoid prefix-based matching like "ae.*" which would incorrectly filter
        // user checkpoints named e.g. "aesthetic_embed.safetensors".
        if (lower is "ae.safetensors" or "ae.gguf") return true;

        // CLIP-L encoders — always start with "clip_l"
        if (lower.StartsWith("clip_l", StringComparison.Ordinal)) return true;

        // T5 encoders (several naming conventions)
        if (lower.StartsWith("t5xxl", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("t5-xxl", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("t5_xxl", StringComparison.Ordinal)) return true;

        // FLUX VAE named explicitly
        if (lower.StartsWith("flux_vae", StringComparison.Ordinal)) return true;

        return false;
    }

    // ── Privátní record pro metadata závislosti ───────────────────────────────

    private sealed record DepInfo(
        string  FileName,
        string  PublicUrl,   // Vyzkoušíme bez tokenu — veřejné nebo polopřístupné
        string? GatedUrl,    // Záloha s tokenem pokud PublicUrl vrátí 401/403
        string  Subdir);
}
