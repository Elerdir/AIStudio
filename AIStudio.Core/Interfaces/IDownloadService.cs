using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public interface IDownloadService
{
    /// <summary>
    /// Stáhne soubor z <paramref name="url"/> do <paramref name="destPath"/>.
    /// Podporuje HuggingFace (Bearer token) i Civitai (?token=...) autentizaci.
    /// </summary>
    Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<DownloadProgressInfo>? progress = null,
        string? apiToken = null,
        CancellationToken ct = default);
}
