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
}
