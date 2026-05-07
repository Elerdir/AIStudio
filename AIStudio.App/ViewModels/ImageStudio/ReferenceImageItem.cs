using Avalonia.Media.Imaging;

namespace AIStudio.App.ViewModels.ImageStudio;

/// <summary>
/// Jeden referenční obrázek v generátoru — cesta + thumbnail pro UI náhled.
/// Thumbnail je lazy-loaded (~80 px wide, dost na náhled v sidebaru).
/// </summary>
public sealed class ReferenceImageItem
{
    public string Path { get; }
    public string FileName { get; }

    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail => _thumbnail ??= LoadThumbnail();

    public ReferenceImageItem(string path)
    {
        Path     = path;
        FileName = System.IO.Path.GetFileName(path);
    }

    private Bitmap? LoadThumbnail()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            using var stream = File.OpenRead(Path);
            return Bitmap.DecodeToWidth(stream, 80);
        }
        catch
        {
            return null;
        }
    }
}
