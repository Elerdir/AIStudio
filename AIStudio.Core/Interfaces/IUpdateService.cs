using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Kontroluje dostupnost novějších verzí aplikace a spustí instalátor.
/// </summary>
public interface IUpdateService
{
    /// <summary>Aktuálně nainstalovaná verze.</summary>
    Version CurrentVersion { get; }

    /// <summary>
    /// Zkontroluje GitHub Releases. Vrátí UpdateInfo pokud je dostupná novější verze,
    /// jinak null.
    /// </summary>
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Stáhne installer nové verze a spustí ho (aplikace se zavře).
    /// </summary>
    Task DownloadAndInstallAsync(UpdateInfo update,
                                  IProgress<DownloadProgressInfo>? progress = null,
                                  CancellationToken ct = default);
}
