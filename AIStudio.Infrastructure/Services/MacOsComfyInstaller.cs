using System.Diagnostics;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// macOS implementace <see cref="IComfyInstaller"/>. Apple nedistribuuje
/// hotový Portable build s embedded Pythonem (ten existuje jen pro Windows
/// NVIDIA), takže instalujeme přes klasický klon + virtual env.
///
/// **Kroky:**
///   1. <c>git clone https://github.com/comfyanonymous/ComfyUI</c>
///   2. <c>python3 -m venv venv</c>
///   3. <c>./venv/bin/pip install -r requirements.txt</c>
///   4. PyTorch s MPS (Metal Performance Shaders) — pip nainstaluje
///      <c>torch torchvision torchaudio</c> bez extra index URL, protože
///      wheels pro Apple Silicon mají Metal built-in.
///
/// **Předpoklady:**
///   • <c>git</c> v PATH — Apple ho dodává jako součást Xcode CLI Tools
///     (uživatel je nainstaluje při prvním pokusu o git příkaz).
///   • <c>python3</c> v PATH — macOS 12.3+ má system Python 3 dostupný
///     přes <c>/usr/bin/python3</c>. Pro starší macOS user musí mít
///     Python 3.11+ z python.org nebo Homebrew.
///
/// **Pozor:** First-time installation může trvat 5–15 minut (PyTorch
/// wheels jsou velké). Progress je report-only přes log; ComfyInstallProgress
/// nemá granularity pro git/pip subprocesses, takže UI ukazuje jen stage messages.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacOsComfyInstaller : IComfyInstaller
{
    private const string ComfyUiRepoUrl = "https://github.com/comfyanonymous/ComfyUI.git";

    public string DefaultInstallDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIStudio", "ComfyUI");

    // ── Detekce existující instalace ──────────────────────────────────────────

    public (string ComfyUiDir, string PythonPath)? DetectExisting(string installDir)
    {
        if (!Directory.Exists(installDir)) return null;

        var comfyUiDir = Path.Combine(installDir, "ComfyUI");
        var venvPython = Path.Combine(installDir, "venv", "bin", "python3");

        if (File.Exists(Path.Combine(comfyUiDir, "main.py")) && File.Exists(venvPython))
            return (comfyUiDir, venvPython);

        return null;
    }

    // ── Hlavní instalace ──────────────────────────────────────────────────────

    /// <summary>
    /// macOS instaluje ComfyUI přes git clone (ne portable .7z), takže „aktualizace na
    /// nejnovější" se dělá přes git cestu (<see cref="IComfyUpdateService"/>), ne tudy.
    /// Tahle metoda se na macOS nevolá — instalace je git repo, takže UI nabízí git tlačítko.
    /// </summary>
    public Task<(string ComfyUiDir, string PythonPath)> UpdateToLatestAsync(
        string installDir, IProgress<ComfyInstallProgress>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Na macOS se ComfyUI aktualizuje přes git (tlačítko 'Sladit s ověřenou verzí'), ne re-extrakcí portable.");

    public async Task<(string ComfyUiDir, string PythonPath)> InstallAsync(
        string                            installDir,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        Directory.CreateDirectory(installDir);

        if (DetectExisting(installDir) is { } existing)
        {
            Log.Information("MacOsComfyInstaller: existující instalace v {Dir}", installDir);
            progress?.Report(new(ComfyInstallStage.Done, "ComfyUI je již nainstalován",
                                 100, 0, 0, 0, null));
            return existing;
        }

        var comfyUiDir = Path.Combine(installDir, "ComfyUI");
        var venvDir    = Path.Combine(installDir, "venv");
        var venvPython = Path.Combine(venvDir, "bin", "python3");

        // ── 1) git clone ──────────────────────────────────────────────────────
        if (!Directory.Exists(Path.Combine(comfyUiDir, ".git")))
        {
            progress?.Report(new(ComfyInstallStage.FetchingRelease,
                "Stahuji ComfyUI z GitHubu (git clone)…", 5, 0, 0, 0, null));
            Log.Information("MacOsComfyInstaller: git clone {Url} → {Dir}", ComfyUiRepoUrl, comfyUiDir);

            await RunAsync("git", $"clone --depth 1 \"{ComfyUiRepoUrl}\" \"{comfyUiDir}\"",
                workingDir: installDir, timeoutMinutes: 10, ct);

            if (!File.Exists(Path.Combine(comfyUiDir, "main.py")))
                throw new InvalidOperationException(
                    "git clone se zdánlivě podařil, ale ComfyUI/main.py chybí. " +
                    "Zkontroluj GitHub dostupnost / proxy nastavení.");
        }

        // ── 2) python3 -m venv ───────────────────────────────────────────────
        if (!File.Exists(venvPython))
        {
            progress?.Report(new(ComfyInstallStage.Extracting,
                "Vytvářím Python virtual environment…", 20, 0, 0, 0, null));
            Log.Information("MacOsComfyInstaller: python3 -m venv {Dir}", venvDir);

            await RunAsync("python3", $"-m venv \"{venvDir}\"",
                workingDir: installDir, timeoutMinutes: 5, ct);

            if (!File.Exists(venvPython))
                throw new InvalidOperationException(
                    "python3 -m venv neselhal, ale venv/bin/python3 chybí. " +
                    "Pravděpodobně chybí python3 v PATH — nainstaluj přes brew nebo z python.org.");
        }

        // ── 3) pip install -r requirements.txt ────────────────────────────────
        progress?.Report(new(ComfyInstallStage.Extracting,
            "Instaluji Python závislosti (může trvat 5–10 minut, PyTorch je velký)…",
            40, 0, 0, 0, null));

        var reqPath = Path.Combine(comfyUiDir, "requirements.txt");
        if (!File.Exists(reqPath))
            throw new InvalidOperationException(
                $"requirements.txt nenalezeno v {comfyUiDir} — neúplný clone?");

        Log.Information("MacOsComfyInstaller: pip install -r requirements.txt (může trvat dlouho)");
        await RunAsync(venvPython,
            $"-m pip install --upgrade pip",
            workingDir: comfyUiDir, timeoutMinutes: 5, ct);

        await RunAsync(venvPython,
            $"-m pip install -r \"{reqPath}\"",
            workingDir: comfyUiDir, timeoutMinutes: 30, ct);

        // ── 4) Verify ────────────────────────────────────────────────────────
        progress?.Report(new(ComfyInstallStage.Finishing, "Ověřuji instalaci…",
            95, 0, 0, 0, null));

        if (DetectExisting(installDir) is not { } verified)
            throw new InvalidOperationException(
                "Po pip install se nepodařilo ověřit funkční instalaci. " +
                $"Zkontroluj {installDir} a podívej se do logu.");

        progress?.Report(new(ComfyInstallStage.Done, "Hotovo!", 100, 0, 0, 0, null));
        Log.Information("MacOsComfyInstaller: instalace hotova → {Comfy} | {Python}",
                        verified.ComfyUiDir, verified.PythonPath);

        return verified;
    }

    // ── ComfyUI-GGUF custom node ──────────────────────────────────────────────

    public bool IsGgufNodeInstalled(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return false;
        var nodePath = Path.Combine(comfyUiDir, "custom_nodes", "ComfyUI-GGUF");
        return Directory.Exists(nodePath)
            && (File.Exists(Path.Combine(nodePath, "nodes.py"))
                || File.Exists(Path.Combine(nodePath, "__init__.py")));
    }

    public async Task EnsureGgufNodeInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsGgufNodeInstalled(comfyUiDir))
        {
            Log.Information("MacOsComfyInstaller: ComfyUI-GGUF custom node už nainstalovaný");
            return;
        }

        var customNodesDir = Path.Combine(comfyUiDir, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);

        progress?.Report(new(ComfyInstallStage.Extracting,
            "Stahuji ComfyUI-GGUF custom node…", 50, 0, 0, 0, null));

        await RunAsync("git",
            "clone --depth 1 https://github.com/city96/ComfyUI-GGUF.git",
            workingDir: customNodesDir, timeoutMinutes: 5, ct);

        // Doinstaluj gguf pip balíček do venv
        Log.Information("MacOsComfyInstaller: pip install gguf");
        await RunAsync(pythonExe, "-m pip install gguf",
            workingDir: comfyUiDir, timeoutMinutes: 5, ct);

        progress?.Report(new(ComfyInstallStage.Done, "ComfyUI-GGUF nainstalován",
            100, 0, 0, 0, null));
    }

    // ── ComfyUI-VideoHelperSuite (VHS_VideoCombine → MP4) ──────────────────────

    public bool IsVideoHelperSuiteInstalled(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return false;
        var nodePath = Path.Combine(comfyUiDir, "custom_nodes", "ComfyUI-VideoHelperSuite");
        return Directory.Exists(nodePath)
            && (File.Exists(Path.Combine(nodePath, "__init__.py"))
                || Directory.Exists(Path.Combine(nodePath, "videohelpersuite")));
    }

    public async Task EnsureVideoHelperSuiteInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsVideoHelperSuiteInstalled(comfyUiDir))
        {
            Log.Information("MacOsComfyInstaller: ComfyUI-VideoHelperSuite už nainstalovaný");
            return;
        }

        var customNodesDir = Path.Combine(comfyUiDir, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);

        progress?.Report(new(ComfyInstallStage.Extracting,
            "Stahuji ComfyUI-VideoHelperSuite…", 50, 0, 0, 0, null));

        await RunAsync("git",
            "clone --depth 1 https://github.com/Kosinkadink/ComfyUI-VideoHelperSuite.git",
            workingDir: customNodesDir, timeoutMinutes: 5, ct);

        var reqPath = Path.Combine(customNodesDir, "ComfyUI-VideoHelperSuite", "requirements.txt");
        if (File.Exists(reqPath))
        {
            Log.Information("MacOsComfyInstaller: pip install -r VideoHelperSuite requirements");
            await RunAsync(pythonExe, $"-m pip install -r \"{reqPath}\"",
                workingDir: comfyUiDir, timeoutMinutes: 10, ct);
        }

        progress?.Report(new(ComfyInstallStage.Done, "ComfyUI-VideoHelperSuite nainstalován",
            100, 0, 0, 0, null));
    }

    // ── FaceDetailer (Impact-Pack + Subpack + detektor) ──────────────────────

    public bool IsFaceDetailerInstalled(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return false;
        var pack  = Path.Combine(comfyUiDir, "custom_nodes", "ComfyUI-Impact-Pack");
        var model = Path.Combine(comfyUiDir, "models", "ultralytics", "bbox", "face_yolov8m.pt");
        return Directory.Exists(pack)
            && (File.Exists(Path.Combine(pack, "__init__.py")) || Directory.Exists(Path.Combine(pack, "modules")))
            && File.Exists(model);
    }

    public async Task EnsureFaceDetailerInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsFaceDetailerInstalled(comfyUiDir))
        {
            Log.Information("MacOsComfyInstaller: FaceDetailer už nainstalovaný");
            return;
        }

        var customNodesDir = Path.Combine(comfyUiDir, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);

        progress?.Report(new(ComfyInstallStage.Extracting, "Stahuji ComfyUI-Impact-Pack…", 40, 0, 0, 0, null));
        if (!Directory.Exists(Path.Combine(customNodesDir, "ComfyUI-Impact-Pack")))
            await RunAsync("git", "clone --depth 1 https://github.com/ltdrdata/ComfyUI-Impact-Pack.git",
                workingDir: customNodesDir, timeoutMinutes: 5, ct);
        if (!Directory.Exists(Path.Combine(customNodesDir, "ComfyUI-Impact-Subpack")))
            await RunAsync("git", "clone --depth 1 https://github.com/ltdrdata/ComfyUI-Impact-Subpack.git",
                workingDir: customNodesDir, timeoutMinutes: 5, ct);

        foreach (var pack in new[] { "ComfyUI-Impact-Pack", "ComfyUI-Impact-Subpack" })
        {
            var req = Path.Combine(customNodesDir, pack, "requirements.txt");
            if (File.Exists(req))
                await RunAsync(pythonExe, $"-m pip install -r \"{req}\"",
                    workingDir: comfyUiDir, timeoutMinutes: 10, ct);
        }

        var modelPath = Path.Combine(comfyUiDir, "models", "ultralytics", "bbox", "face_yolov8m.pt");
        if (!File.Exists(modelPath))
        {
            progress?.Report(new(ComfyInstallStage.Downloading, "Stahuji detekční model obličeje…", 90, 0, 0, 0, null));
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            await RunAsync("curl",
                $"-L -o \"{modelPath}\" https://huggingface.co/Bingsu/adetailer/resolve/main/face_yolov8m.pt",
                workingDir: comfyUiDir, timeoutMinutes: 10, ct);
        }

        progress?.Report(new(ComfyInstallStage.Done, "FaceDetailer nainstalován", 100, 0, 0, 0, null));
    }

    // ── ComfyUI-Frame-Interpolation (RIFE VFI → plynulejší video) ─────────────

    public bool IsFrameInterpolationInstalled(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return false;
        var nodePath = Path.Combine(comfyUiDir, "custom_nodes", "ComfyUI-Frame-Interpolation");
        return Directory.Exists(nodePath)
            && (File.Exists(Path.Combine(nodePath, "__init__.py"))
                || Directory.Exists(Path.Combine(nodePath, "vfi_models")));
    }

    public async Task EnsureFrameInterpolationInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        if (IsFrameInterpolationInstalled(comfyUiDir))
        {
            Log.Information("MacOsComfyInstaller: ComfyUI-Frame-Interpolation už nainstalovaný");
            return;
        }

        var customNodesDir = Path.Combine(comfyUiDir, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);

        progress?.Report(new(ComfyInstallStage.Extracting,
            "Stahuji ComfyUI-Frame-Interpolation…", 50, 0, 0, 0, null));

        if (!Directory.Exists(Path.Combine(customNodesDir, "ComfyUI-Frame-Interpolation")))
            await RunAsync("git",
                "clone --depth 1 https://github.com/Fannovel16/ComfyUI-Frame-Interpolation.git",
                workingDir: customNodesDir, timeoutMinutes: 5, ct);

        var nodeDir = Path.Combine(customNodesDir, "ComfyUI-Frame-Interpolation");
        var reqPath = Path.Combine(nodeDir, "requirements-no-cupy.txt");
        if (!File.Exists(reqPath)) reqPath = Path.Combine(nodeDir, "requirements.txt");
        if (File.Exists(reqPath))
            await RunAsync(pythonExe, $"-m pip install -r \"{reqPath}\"",
                workingDir: comfyUiDir, timeoutMinutes: 10, ct);

        progress?.Report(new(ComfyInstallStage.Done, "ComfyUI-Frame-Interpolation nainstalován",
            100, 0, 0, 0, null));
    }

    // ── DirectML — N/A na macOS ──────────────────────────────────────────────

    public bool IsDirectMlInstalled(string pythonExe) => false;

    public Task EnsureDirectMlInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default)
    {
        // DirectML je Windows-only (Microsoft DirectX). Na macOS používáme Metal
        // a PyTorch wheels mají MPS backend integrovaný, žádný extra install.
        Log.Debug("MacOsComfyInstaller: DirectML není na macOS dostupné, no-op");
        return Task.CompletedTask;
    }

    // ── Process runner ────────────────────────────────────────────────────────

    /// <summary>
    /// Spustí process s timeoutem, streamuje stdout/stderr do logu.
    /// Vyhodí <see cref="InvalidOperationException"/> pokud proces vrátí
    /// nenulový exit code nebo timeout vyprší.
    /// </summary>
    private static async Task RunAsync(
        string            fileName,
        string            arguments,
        string            workingDir,
        int               timeoutMinutes,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        Log.Information("MacOsComfyInstaller: {Cmd} {Args} (cwd={Cwd})",
                        fileName, arguments, workingDir);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Nelze spustit {fileName}");

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) Log.Information("[{Cmd}] {Line}", fileName, e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) Log.Warning("[{Cmd}] {Line}", fileName, e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException(
                $"{fileName} timeout po {timeoutMinutes} minutách — pravděpodobně síťový problém nebo zaseknutý pip.");
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} skončilo s kódem {proc.ExitCode} — detail v logu.");
    }
}
