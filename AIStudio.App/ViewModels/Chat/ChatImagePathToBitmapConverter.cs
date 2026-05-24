using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Serilog;

namespace AIStudio.App.ViewModels.Chat;

/// <summary>
/// Konvertuje cestu k obrázku (string) na <see cref="Bitmap"/> pro Image kontrolu.
/// Bezpečně zvládne null / chybějící soubor / neplatný obrázek — vrátí null,
/// Avalonia v takovém případě prostě nic nevykreslí.
///
/// <para>Pozn.: Tohle se volá při každém update binding, tedy potenciálně několikrát
/// za zprávu. Pro chat scénář (max desítky obrázků v konverzaci) je to OK; pro
/// scénář s tisíci obrázky bychom potřebovali cache.</para>
/// </summary>
public sealed class ChatImagePathToBitmapConverter : IValueConverter
{
    public static readonly ChatImagePathToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path)) return null;

        try
        {
            // Stream-based load + okamžitě dispose, aby soubor nezůstal uzamknutý.
            // Bitmap si data zkopíruje do paměti, takže po dispose streamu je
            // pořád použitelná pro rendering.
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ChatImagePathToBitmapConverter: load failed for {Path}", path);
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
