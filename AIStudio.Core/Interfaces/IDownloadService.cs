using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public interface IDownloadService
{
    /// <summary>
    /// Stáhne soubor z <paramref name="url"/> do <paramref name="destPath"/>.
    /// Podporuje HuggingFace (Bearer token) i Civitai (?token=...) autentizaci.
    /// Pokud je zadán <paramref name="expectedSha256"/>, po stažení ověří integritu
    /// a v případě neshody vyhodí <see cref="ChecksumMismatchException"/>.
    /// </summary>
    Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<DownloadProgressInfo>? progress = null,
        string? apiToken = null,
        CancellationToken ct = default,
        string? expectedSha256 = null);
}
