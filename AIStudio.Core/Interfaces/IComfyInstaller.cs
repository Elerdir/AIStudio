using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Auto-instalátor ComfyUI Portable. Stáhne nejnovější Windows portable
/// build z GitHubu, rozbalí ho a vrátí cesty potřebné pro spouštění.
/// </summary>
public interface IComfyInstaller
{
    /// <summary>
    /// Defaultní cesta pro instalaci — typicky <c>%LocalAppData%\AIStudio\ComfyUI</c>.
    /// </summary>
    string DefaultInstallDirectory { get; }

    /// <summary>
    /// Zkontroluje, jestli v <paramref name="installDir"/> již existuje funkční
    /// instalace (najde <c>main.py</c> a <c>python.exe</c>). Vrátí cesty pokud ano.
    /// </summary>
    (string ComfyUiDir, string PythonPath)? DetectExisting(string installDir);

    /// <summary>
    /// Stáhne ComfyUI Portable .7z z GitHubu, rozbalí do <paramref name="installDir"/>
    /// a vrátí cesty k <c>main.py</c> a <c>python.exe</c>.
    /// </summary>
    /// <exception cref="OperationCanceledException">Pokud byla operace zrušena.</exception>
    /// <exception cref="InvalidOperationException">Pokud asset nenalezen / archiv neplatný.</exception>
    Task<(string ComfyUiDir, string PythonPath)> InstallAsync(
        string                            installDir,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default);

    /// <summary>
    /// True pokud je v ComfyUI nainstalovaný custom node ComfyUI-GGUF
    /// (potřebný pro FLUX GGUF kvantizace).
    /// </summary>
    bool IsGgufNodeInstalled(string comfyUiDir);

    /// <summary>
    /// Pokud chybí custom node ComfyUI-GGUF, stáhne ho z GitHubu, rozbalí
    /// do <c>custom_nodes/ComfyUI-GGUF/</c> a doinstaluje pip závislost
    /// <c>gguf</c> přes embedded Python. Pokud už existuje, vrátí ihned.
    /// </summary>
    Task EnsureGgufNodeInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default);

    /// <summary>
    /// True pokud je nainstalovaný custom node ComfyUI-VideoHelperSuite
    /// (poskytuje <c>VHS_VideoCombine</c> pro MP4 výstup video generace).
    /// </summary>
    bool IsVideoHelperSuiteInstalled(string comfyUiDir);

    /// <summary>
    /// Pokud chybí ComfyUI-VideoHelperSuite, stáhne ho z GitHubu do
    /// <c>custom_nodes/ComfyUI-VideoHelperSuite/</c> a doinstaluje jeho
    /// <c>requirements.txt</c> (vč. imageio-ffmpeg → ffmpeg pro MP4). Idempotentní.
    /// </summary>
    Task EnsureVideoHelperSuiteInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default);

    /// <summary>
    /// True pokud je nainstalovaný ComfyUI-Impact-Pack (+ Subpack) a detekční model
    /// obličeje — poskytují <c>FaceDetailer</c> + <c>UltralyticsDetectorProvider</c>.
    /// </summary>
    bool IsFaceDetailerInstalled(string comfyUiDir);

    /// <summary>
    /// Doinstaluje ComfyUI-Impact-Pack + ComfyUI-Impact-Subpack (custom nody) + jejich
    /// pip závislosti + stáhne detekční model <c>face_yolov8m.pt</c> do
    /// <c>models/ultralytics/bbox/</c>. Idempotentní.
    /// </summary>
    Task EnsureFaceDetailerInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default);

    /// <summary>
    /// True pokud je nainstalovaný custom node ComfyUI-Frame-Interpolation
    /// (poskytuje <c>RIFE VFI</c> pro dopočítání mezisnímků = plynulejší video).
    /// </summary>
    bool IsFrameInterpolationInstalled(string comfyUiDir);

    /// <summary>
    /// Doinstaluje ComfyUI-Frame-Interpolation (RIFE interpolace) + jeho pip závislosti.
    /// RIFE model se dotáhne automaticky až při prvním běhu nodu. Idempotentní.
    /// </summary>
    Task EnsureFrameInterpolationInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default);

    /// <summary>True pokud je v embedded Pythonu nainstalován <c>torch-directml</c>.</summary>
    bool IsDirectMlInstalled(string pythonExe);

    /// <summary>
    /// Doinstaluje <c>torch-directml</c> přes pip do embedded Pythonu ComfyUI
    /// Portable. Volá se po základní instalaci ComfyUI pokud detekovaná GPU je
    /// AMD/Intel — DirectML je jejich jediná cesta k akceleraci image gen na
    /// Windows. Idempotentní: pokud už je nainstalován, vrátí ihned.
    ///
    /// ComfyService pak musí ComfyUI spouštět s <c>--directml</c> flagem.
    /// </summary>
    Task EnsureDirectMlInstalledAsync(
        string                            comfyUiDir,
        string                            pythonExe,
        IProgress<ComfyInstallProgress>?  progress = null,
        CancellationToken                 ct       = default);
}
