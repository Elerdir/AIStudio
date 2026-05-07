using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace AIStudio.App.Converters;

/// <summary>
/// Vrátí jméno souboru bez koncovky — užitečné pro ComboBox itemy, kde uživatel
/// vidí čistý název modelu („flux1-schnell-Q4_K_S") místo file namu se příponou
/// („flux1-schnell-Q4_K_S.gguf"). Hodnota v bindingu zůstává plná pro logiku
/// ComfyUI, jen Display je stripnuté.
/// </summary>
public sealed class FileNameWithoutExtensionConverter : IValueConverter
{
    public static readonly FileNameWithoutExtensionConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrEmpty(s)) return value;

        // Path.GetFileNameWithoutExtension umí cestu i samotné jméno — vyhodí
        // i potenciální podsložku v stringu (FLUX deps mohou být v "vae/ae.safetensors").
        try
        {
            return Path.GetFileNameWithoutExtension(s);
        }
        catch
        {
            return s;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value;   // jednosměrný — UI display only
}
