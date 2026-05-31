using System.IO.Compression;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Plně automatická instalace PuLID-Flux stacku (identita osoby bez tréninku).
/// Recept ověřený živě na ComfyUI portable (Python 3.12):
///
/// <list type="number">
/// <item>Custom node <c>ComfyUI_PuLID_Flux_ll</c> — ZIP z GitHubu (bez git závislosti),
///   rozbalí do <c>custom_nodes/</c>.</item>
/// <item>Python deps do embedded Pythonu — insightface (1.0.1 pure-Python wheel,
///   žádný build tools), facexlib, facenet-pytorch (--no-deps), cython, ftfy, timm.</item>
/// <item>PuLID model (~1.1 GB) → <c>models/pulid/</c>.</item>
/// <item>antelopev2 (InsightFace) — auto-download přes Python + zploštění dvojitého
///   zanoření (<c>antelopev2/antelopev2/*.onnx → antelopev2/*.onnx</c>), jinak
///   insightface hodí <c>AssertionError: 'detection' in self.models</c>.</item>
/// </list>
///
/// <para>EVA-CLIP se dotáhne sám při prvním běhu workflow (HF cache).</para>
/// </summary>
public sealed class PuLIDDependencyService : IPuLIDService
{
    private readonly ISettingsService _settings;
    private readonly IDownloadService _downloader;

    private const string NodeFolder  = "ComfyUI_PuLID_Flux_ll";
    private const string NodeZipUrl   = "https://github.com/lldacing/ComfyUI_PuLID_Flux_ll/archive/refs/heads/main.zip";
    private const string PulidModelFile = "pulid_flux_v0.9.1.safetensors";
    private const string PulidModelUrl  = "https://huggingface.co/guozinan/PuLID/resolve/main/pulid_flux_v0.9.1.safetensors";

    // Detection model z antelopev2 — jeho přítomnost (zploštěná) = antelopev2 ready.
    private const string AntelopeDetModel = "scrfd_10g_bnkps.onnx";

    private static readonly string[] PipPackages = { "insightface", "facexlib", "cython", "ftfy", "timm" };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool   _isInstalling;
    private volatile string _statusLine = string.Empty;

    public PuLIDDependencyService(ISettingsService settings, IDownloadService downloader)
    {
        _settings   = settings;
        _downloader = downloader;
    }

    public string PulidModelFileName => PulidModelFile;
    public bool   IsInstalling       => _isInstalling;
    public string StatusLine         => _statusLine;

    // ── Cesty v ComfyUI ────────────────────────────────────────────────────────

    private string ComfyDir   => _settings.Settings.ComfyUiDirectory ?? string.Empty;
    private string PythonExe   => _settings.Settings.PythonPath ?? string.Empty;
    private string NodePath        => Path.Combine(ComfyDir, "custom_nodes", NodeFolder);
    private string PulidModelPath  => Path.Combine(ComfyDir, "models", "pulid", PulidModelFile);
    private string InsightFaceDir  => Path.Combine(ComfyDir, "models", "insightface");
    private string AntelopePath    => Path.Combine(InsightFaceDir, "models", "antelopev2", AntelopeDetModel);

    // ── Dostupnost ─────────────────────────────────────────────────────────────

    public bool IsAvailable()
    {
        if (string.IsNullOrWhiteSpace(ComfyDir)) return false;
        return Directory.Exists(NodePath)
            && File.Exists(PulidModelPath)
            && File.Exists(AntelopePath);
    }

    // ── Instalace ──────────────────────────────────────────────────────────────

    public async Task EnsureAsync(IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ComfyDir) || string.IsNullOrWhiteSpace(PythonExe))
        {
            Log.Warning("PuLIDDependencyService: chybí ComfyUiDirectory / PythonPath — PuLID nelze instalovat");
            return;
        }
        if (IsAvailable()) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (IsAvailable()) return;
            _isInstalling = true;

            // 1) Custom node (ZIP, bez git)
            if (!Directory.Exists(NodePath))
            {
                _statusLine = "Instaluji PuLID custom node…";
                await InstallNodeAsync(ct);
            }
            // 1b) Kompat patch — novější ComfyUI posílá do forward_orig 'timestep_zero_index',
            //     které lldacing node (zatím) nezná. Idempotentní.
            PatchNodeCompat();

            // 2) Python deps
            _statusLine = "Instaluji Python závislosti (insightface…)";
            await PipInstallAsync(ct);

            // 3) PuLID model
            if (!File.Exists(PulidModelPath))
            {
                _statusLine = "Stahuji PuLID model (~1.1 GB)…";
                await DownloadPulidModelAsync(progress, ct);
            }

            // 4) antelopev2 (download + flatten)
            if (!File.Exists(AntelopePath))
            {
                _statusLine = "Stahuji InsightFace antelopev2…";
                await EnsureAntelopeV2Async(ct);
            }

            Log.Information("PuLIDDependencyService: hotovo (available={Avail})", IsAvailable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "PuLIDDependencyService: instalace selhala");
        }
        finally
        {
            _isInstalling = false;
            _statusLine   = string.Empty;
            _lock.Release();
        }
    }

    private async Task InstallNodeAsync(CancellationToken ct)
    {
        var customNodes = Path.Combine(ComfyDir, "custom_nodes");
        Directory.CreateDirectory(customNodes);

        var tmpZip = Path.Combine(customNodes, NodeFolder + ".zip.tmp");
        try
        {
            Log.Information("PuLIDDependencyService: stahuji node ZIP");
            await _downloader.DownloadFileAsync(NodeZipUrl, tmpZip, progress: null, apiToken: null, ct: ct);

            var extractTmp = Path.Combine(customNodes, NodeFolder + "_extract.tmp");
            if (Directory.Exists(extractTmp)) Directory.Delete(extractTmp, recursive: true);
            ZipFile.ExtractToDirectory(tmpZip, extractTmp);

            // GitHub ZIP rozbalí do podsložky "ComfyUI_PuLID_Flux_ll-main" — přesuneme na finální jméno.
            var inner = Directory.GetDirectories(extractTmp).FirstOrDefault() ?? extractTmp;
            if (Directory.Exists(NodePath)) Directory.Delete(NodePath, recursive: true);
            Directory.Move(inner, NodePath);
            if (Directory.Exists(extractTmp)) Directory.Delete(extractTmp, recursive: true);

            Log.Information("PuLIDDependencyService: node rozbalen → {Path}", NodePath);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Přidá <c>timestep_zero_index=None</c> do signatury <c>pulid_forward_orig</c>
    /// v <c>PulidFluxHook.py</c>. Novější ComfyUI tuhle keyword posílá do
    /// <c>forward_orig</c>, ale lldacing node ji (zatím) nemá → <c>TypeError</c>
    /// při samplingu. PuLID ji v těle nepoužívá, jen ji musí přijmout. Idempotentní.
    /// </summary>
    private void PatchNodeCompat()
    {
        try
        {
            var hook = Path.Combine(NodePath, "PulidFluxHook.py");
            if (!File.Exists(hook)) return;

            var content = File.ReadAllText(hook);
            if (content.Contains("timestep_zero_index")) return;   // už OK / kompatibilní verze

            // Cílíme přesně signaturu pulid_forward_orig (guidance → control → transformer_options).
            foreach (var nl in new[] { "\n", "\r\n" })
            {
                var marker = $"    guidance: Tensor = None,{nl}    control = None,{nl}    transformer_options={{}},";
                if (content.Contains(marker))
                {
                    var patched = $"    guidance: Tensor = None,{nl}    control = None,{nl}    timestep_zero_index=None,{nl}    transformer_options={{}},";
                    File.WriteAllText(hook, content.Replace(marker, patched));
                    Log.Information("PuLIDDependencyService: PulidFluxHook patchnut (timestep_zero_index) pro novější ComfyUI");
                    return;
                }
            }
            Log.Warning("PuLIDDependencyService: signatura pulid_forward_orig nenalezena — patch přeskočen (jiná verze node?)");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PuLIDDependencyService: kompat patch selhal");
        }
    }

    private async Task PipInstallAsync(CancellationToken ct)
    {
        // Hlavní balíčky (insightface si přitáhne onnxruntime + onnx).
        await RunPythonAsync(
            $"-s -m pip install --no-warn-script-location {string.Join(' ', PipPackages)}", ct,
            ignoreExitCode: false);

        // facenet-pytorch musí být --no-deps (jinak chce torch<2.3).
        await RunPythonAsync(
            "-s -m pip install --no-warn-script-location --no-deps facenet-pytorch", ct,
            ignoreExitCode: true);   // selhání tady není fatal — node ho používá jen pro některé cesty
    }

    private async Task DownloadPulidModelAsync(IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PulidModelPath)!);
        var tmp = PulidModelPath + ".tmp";
        try
        {
            await _downloader.DownloadFileAsync(PulidModelUrl, tmp, progress, apiToken: null, ct: ct);
            if (File.Exists(PulidModelPath)) File.Delete(PulidModelPath);
            File.Move(tmp, PulidModelPath);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Stáhne antelopev2 přes insightface (auto-download) a zploští dvojité zanoření.
    /// Vše v jednom Python skriptu — FaceAnalysis init stáhne modely, pak (i když
    /// hodí AssertionError kvůli nested struktuře) skript přesune onnx o úroveň výš.
    /// </summary>
    private async Task EnsureAntelopeV2Async(CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"aistudio_antelope_{Guid.NewGuid():N}.py");
        var script =
            "import os, shutil\n" +
            "root = r'''" + InsightFaceDir + "'''\n" +
            "try:\n" +
            "    from insightface.app import FaceAnalysis\n" +
            "    FaceAnalysis(name='antelopev2', root=root, providers=['CPUExecutionProvider'])\n" +
            "except Exception as e:\n" +
            "    print('insightface init (ocekavano pred flatten):', e)\n" +
            "base = os.path.join(root, 'models', 'antelopev2')\n" +
            "inner = os.path.join(base, 'antelopev2')\n" +
            "if os.path.isdir(inner):\n" +
            "    for f in os.listdir(inner):\n" +
            "        if f.endswith('.onnx'):\n" +
            "            shutil.move(os.path.join(inner, f), os.path.join(base, f))\n" +
            "    shutil.rmtree(inner, ignore_errors=True)\n" +
            "z = os.path.join(root, 'models', 'antelopev2.zip')\n" +
            "if os.path.exists(z):\n" +
            "    os.remove(z)\n" +
            "print('ANTELOPEV2_READY' if os.path.exists(os.path.join(base, 'scrfd_10g_bnkps.onnx')) else 'ANTELOPEV2_MISSING')\n";

        await File.WriteAllTextAsync(scriptPath, script, ct);
        try
        {
            await RunPythonAsync($"-s \"{scriptPath}\"", ct, ignoreExitCode: true);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }

    private async Task RunPythonAsync(string arguments, CancellationToken ct, bool ignoreExitCode)
    {
        var result = await ProcessRunner.RunAsync(
            new ProcessRunOptions
            {
                FileName  = PythonExe,
                Arguments = arguments,
                TailSize  = 40,
            },
            onLine: line => Log.Information("[pulid-setup] {Line}", line),
            ct:     ct);

        if (!result.Success && !ignoreExitCode)
            throw new InvalidOperationException(
                $"PuLID setup příkaz selhal (exit {result.ExitCode}):\n{result.TailText}");
    }
}
