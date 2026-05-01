namespace AIStudio.Core.Models;

/// <summary>
/// Progress snapshot pro stahování souboru.
/// </summary>
public record DownloadProgressInfo(
    long Downloaded,
    long Total,
    double BytesPerSecond)
{
    public double Percent => Total > 0 ? (double)Downloaded / Total * 100.0 : 0;
}
