using System.Diagnostics;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Default implementace <see cref="ILoraTrainerDependencyService"/>.
/// Reuse-uje ComfyUI Python venv — instalujeme pip balíky tam.
/// sd-scripts klonujeme do <c>%LocalAppData%\AIStudio\sd-scripts\</c>.
///
/// <para>Pip balíky se kontrolují přes <c>python -m pip show {pkg}</c> —
/// rychlejší než <c>pip list</c> protože se ptáme jen na konkrétní balíky.</para>
///
/// <para>Git clone používá <c>git</c> z PATH. Pokud git není k dispozici,
/// padáme na fallback: stáhnout ZIP z GitHub release URL a rozbalit.</para>
/// </summary>
public sealed class LoraTrainerDependencyService : ILoraTrainerDependencyService
{
    /// <summary>
    /// Pip balíky potřebné pro sd-scripts trénink. Veškeré jsou cross-platform,
    /// kromě <c>bitsandbytes</c> který má na Windows problémy s vlastním
    /// kompilovaným .pyd — řešíme přes oficiální Windows wheel.
    /// Verze záměrně nepinujeme — pip si vyřeší kompatibilní set.
    /// </summary>
    private static readonly string[] RequiredPipPackages =
    {
        "accelerate",
        "transformers",
        "diffusers",
        "peft",
        "bitsandbytes",
        "safetensors",
        "lycoris-lora",
        "prodigyopt",
        "lion-pytorch",
        "einops",
        "ftfy",
        "opencv-python",
        "pytorch-lightning",
        "voluptuous",
        "open-clip-torch",
    };

    private const string SdScriptsRepoUrl = "https://github.com/kohya-ss/sd-scripts.git";

    /// <summary>Kde si držíme sd-scripts repo na disku (mimo ComfyUI, izolovaně).</summary>
    private static readonly string SdScriptsRoot =
        Path.Combine(AppPaths.AppDataRoot, "sd-scripts");

    public bool    IsInstalling       { get; private set; }
    public string  CurrentStage       { get; private set; } = string.Empty;
    public string? SdScriptsDirectory => Directory.Exists(SdScriptsRoot) ? SdScriptsRoot : null;

    public bool IsSdScriptsAvailable() =>
        Directory.Exists(SdScriptsRoot) &&
        File.Exists(Path.Combine(SdScriptsRoot, "sdxl_train_network.py"));

    public async Task<bool> ArePipDependenciesInstalledAsync(string pythonExe, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe)) return false;

        foreach (var pkg in RequiredPipPackages)
        {
            if (!await IsPipPackageInstalledAsync(pythonExe, pkg, ct)) return false;
        }
        return true;
    }

    public async Task<IReadOnlyList<string>> FindMissingAsync(string pythonExe, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
            return RequiredPipPackages.ToList();

        var missing = new List<string>();
        foreach (var pkg in RequiredPipPackages)
        {
            if (!await IsPipPackageInstalledAsync(pythonExe, pkg, ct))
                missing.Add(pkg);
        }
        if (!IsSdScriptsAvailable())
            missing.Add("sd-scripts");
        return missing;
    }

    public async Task EnsureAllAsync(
        string                                 pythonExe,
        IProgress<LoraTrainerInstallProgress>? progress = null,
        CancellationToken                      ct       = default)
    {
        if (IsInstalling) return;

        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
            throw new InvalidOperationException(
                "Python interpreter z ComfyUI nebyl nalezen — nejdřív dokonči ComfyUI instalaci.");

        IsInstalling = true;
        try
        {
            // ── 1) Pip balíky ─────────────────────────────────────────────────
            CurrentStage = "Kontroluji pip balíky…";
            progress?.Report(new LoraTrainerInstallProgress(CurrentStage, null));

            var missingPip = new List<string>();
            foreach (var pkg in RequiredPipPackages)
            {
                ct.ThrowIfCancellationRequested();
                if (!await IsPipPackageInstalledAsync(pythonExe, pkg, ct))
                    missingPip.Add(pkg);
            }

            if (missingPip.Count > 0)
            {
                CurrentStage = $"Instaluji pip balíky ({missingPip.Count})…";
                Log.Information("LoraTrainerDependency: chybí pip balíky: {Packages}", string.Join(", ", missingPip));
                progress?.Report(new LoraTrainerInstallProgress(CurrentStage, null));
                await PipInstallAsync(pythonExe, missingPip, progress, ct);
            }
            else
            {
                Log.Information("LoraTrainerDependency: všechny pip balíky už nainstalovány");
            }

            // ── 2) sd-scripts repo ────────────────────────────────────────────
            if (!IsSdScriptsAvailable())
            {
                CurrentStage = "Klonuji sd-scripts…";
                progress?.Report(new LoraTrainerInstallProgress(CurrentStage, null));
                await CloneSdScriptsAsync(progress, ct);
            }
            else
            {
                Log.Information("LoraTrainerDependency: sd-scripts už dostupné v {Dir}", SdScriptsRoot);
            }

            CurrentStage = "Hotovo";
            progress?.Report(new LoraTrainerInstallProgress(CurrentStage, 100));
        }
        finally
        {
            IsInstalling = false;
        }
    }

    // ── Pip helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Spustí <c>python -m pip show {pkg}</c>. Exit code 0 = balík přítomen.
    /// Output nezajímá nás (jen rychlejší než pip list celý).
    /// </summary>
    private static async Task<bool> IsPipPackageInstalledAsync(
        string pythonExe, string packageName, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = pythonExe,
                Arguments              = $"-m pip show \"{packageName}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraTrainerDependency: pip show {Pkg} selhal", packageName);
            return false;
        }
    }

    /// <summary>
    /// Spustí <c>python -m pip install [packages...]</c>. Streamuje výstup do
    /// <see cref="Log"/> a progress (každý "Installing" hlásí). Bitsandbytes na
    /// Windows často padá pokud chybí MSVC runtime — to nemůžeme řešit z appky,
    /// hlásíme čistě "selhalo, viz log".
    /// </summary>
    private static async Task PipInstallAsync(
        string                                 pythonExe,
        IReadOnlyList<string>                  packages,
        IProgress<LoraTrainerInstallProgress>? progress,
        CancellationToken                      ct)
    {
        var args = "-m pip install --upgrade --no-warn-script-location " +
                   string.Join(" ", packages.Select(p => $"\"{p}\""));

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = args,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var p = new Process { StartInfo = psi };
        var stderr = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Log.Information("[pip] {Line}", e.Data);
            // Heuristika: "Collecting X" / "Installing collected packages: X"
            // → vrátíme uživateli aktuální stage
            if (e.Data.StartsWith("Collecting ", StringComparison.Ordinal) ||
                e.Data.StartsWith("Installing ", StringComparison.Ordinal) ||
                e.Data.StartsWith("Downloading ", StringComparison.Ordinal))
            {
                progress?.Report(new LoraTrainerInstallProgress(e.Data.Trim(), null));
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            Log.Warning("[pip-err] {Line}", e.Data);
        };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            throw;
        }

        if (p.ExitCode != 0)
        {
            var msg = stderr.ToString().Trim();
            throw new InvalidOperationException(
                $"pip install selhal (exit code {p.ExitCode}). Detail v logu. " +
                (string.IsNullOrEmpty(msg) ? "" : $"Stderr: {msg[..Math.Min(msg.Length, 300)]}"));
        }
    }

    // ── sd-scripts clone ──────────────────────────────────────────────────────

    /// <summary>
    /// Naklonuje sd-scripts repo přes <c>git clone</c>. Pokud git není v PATH,
    /// fallback na HTTPS ZIP download. Repo má ~30 MB.
    /// </summary>
    private static async Task CloneSdScriptsAsync(
        IProgress<LoraTrainerInstallProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.AppDataRoot);

        if (Directory.Exists(SdScriptsRoot))
        {
            // Nepouštíme update — vyžádá explicitní akci uživatele
            return;
        }

        // Pokus 1: git clone
        if (await TryGitCloneAsync(progress, ct)) return;

        // Pokus 2: ZIP download
        Log.Warning("LoraTrainerDependency: git není dostupný, stahuji sd-scripts jako ZIP");
        await DownloadSdScriptsZipAsync(progress, ct);
    }

    private static async Task<bool> TryGitCloneAsync(
        IProgress<LoraTrainerInstallProgress>? progress, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "git",
                Arguments              = $"clone --depth 1 \"{SdScriptsRepoUrl}\" \"{SdScriptsRoot}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = new Process { StartInfo = psi };

            p.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Information("[git] {Line}", e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Log.Information("[git] {Line}", e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                throw;
            }

            if (p.ExitCode == 0 &&
                File.Exists(Path.Combine(SdScriptsRoot, "sdxl_train_network.py")))
            {
                progress?.Report(new LoraTrainerInstallProgress("sd-scripts naklonováno", 90));
                return true;
            }

            // Klonování selhalo — uklidíme polovičatou složku ať fallback může začít
            try { if (Directory.Exists(SdScriptsRoot)) Directory.Delete(SdScriptsRoot, recursive: true); }
            catch { }
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // git není v PATH — to není chyba, fallback to ZIP
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraTrainerDependency: git clone selhal");
            return false;
        }
    }

    private static async Task DownloadSdScriptsZipAsync(
        IProgress<LoraTrainerInstallProgress>? progress, CancellationToken ct)
    {
        const string zipUrl = "https://github.com/kohya-ss/sd-scripts/archive/refs/heads/main.zip";
        var tempZip = Path.Combine(Path.GetTempPath(), $"sd-scripts-{Guid.NewGuid():N}.zip");

        try
        {
            progress?.Report(new LoraTrainerInstallProgress("Stahuji sd-scripts ZIP…", null));

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var resp = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buffer = new byte[81_920];
                long downloaded = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    var pct = total.HasValue ? (int)(downloaded * 100 / total.Value) : (int?)null;
                    progress?.Report(new LoraTrainerInstallProgress(
                        $"Stahuji sd-scripts ({downloaded / 1_048_576} MB)…",
                        pct, downloaded, total));
                }
            }

            progress?.Report(new LoraTrainerInstallProgress("Rozbaluji sd-scripts…", null));

            var tempExtract = Path.Combine(Path.GetTempPath(), $"sd-scripts-ext-{Guid.NewGuid():N}");
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract);

            // ZIP obsahuje jednu top-level složku „sd-scripts-main/" — přesuneme
            // její obsah do SdScriptsRoot
            var firstDir = Directory.GetDirectories(tempExtract).FirstOrDefault()
                ?? throw new InvalidOperationException("sd-scripts ZIP má neočekávanou strukturu");

            if (Directory.Exists(SdScriptsRoot))
                Directory.Delete(SdScriptsRoot, recursive: true);
            Directory.Move(firstDir, SdScriptsRoot);

            try { Directory.Delete(tempExtract, recursive: true); } catch { }
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
