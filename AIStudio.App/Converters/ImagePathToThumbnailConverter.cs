using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Serilog;

namespace AIStudio.App.Converters;

/// <summary>
/// Načte cestu k obrázku jako malý <see cref="Bitmap"/> náhled — pro thumbnaily
/// příloh v chat inputu. Dekóduje na šířku ~120 px (DecodeToWidth), aby velké
/// fotky nezabíraly RAM ani nezdržely UI. Při chybě / neexistenci vrací null
/// (Image control prostě nic neukáže).
/// </summary>
public sealed class ImagePathToThumbnailConverter : IValueConverter
{
    public static readonly ImagePathToThumbnailConverter Instance = new();

    private const int ThumbWidth = 240;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, ThumbWidth);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImagePathToThumbnailConverter: nelze načíst náhled {Path}", path);
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
