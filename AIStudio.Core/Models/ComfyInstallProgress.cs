namespace AIStudio.Core.Models;

/// <summary>Stage instalace ComfyUI Portable — viz <see cref="ComfyInstallProgress"/>.</summary>
public enum ComfyInstallStage
{
    /// <summary>Před zahájením.</summary>
    Idle,
    /// <summary>Volání GitHub API pro nalezení latest release.</summary>
    FetchingRelease,
    /// <summary>Stahování .7z archivu z GitHubu.</summary>
    Downloading,
    /// <summary>Rozbalování archivu na disk.</summary>
    Extracting,
    /// <summary>Závěrečné kroky — odstranění .7z, ověření cest.</summary>
    Finishing,
    /// <summary>Hotovo, ComfyUI je připraven k použití.</summary>
    Done,
    /// <summary>Selhání — viz <see cref="ComfyInstallProgress.Error"/>.</summary>
    Error
}

/// <summary>
/// Progress report při auto-instalaci ComfyUI Portable. Posílá se přes
/// <see cref="System.IProgress{T}"/> z installeru do UI vrstvy.
/// </summary>
public record ComfyInstallProgress(
    ComfyInstallStage Stage,
    string            Message,
    int               Percent,           // 0–100, sdílené mezi všemi stage
    long              DownloadedBytes,   // jen pro Downloading
    long              TotalBytes,        // jen pro Downloading
    double            BytesPerSecond,    // jen pro Downloading
    string?           Error              // jen pro Error stage
);
