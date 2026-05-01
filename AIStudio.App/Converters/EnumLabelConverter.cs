using System.Globalization;
using Avalonia.Data.Converters;
using AIStudio.App.ViewModels.ImageStudio;

namespace AIStudio.App.Converters;

/// <summary>Converts AspectRatio enum → user-friendly string ("1 : 1", "16 : 9" …).</summary>
public sealed class AspectRatioLabelConverter : IValueConverter
{
    public static readonly AspectRatioLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AspectRatio r ? r switch
        {
            AspectRatio.R1x1  => "1 : 1",
            AspectRatio.R16x9 => "16 : 9",
            AspectRatio.R9x16 => "9 : 16",
            AspectRatio.R4x3  => "4 : 3",
            AspectRatio.R3x4  => "3 : 4",
            AspectRatio.R21x9 => "21 : 9",
            _                 => value.ToString()
        } : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts ImageQuality enum → user-friendly string ("SD (512 px)", "FHD (1024 px)" …).</summary>
public sealed class ImageQualityLabelConverter : IValueConverter
{
    public static readonly ImageQualityLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ImageQuality q ? q switch
        {
            ImageQuality.SD    => "SD  (512 px)",
            ImageQuality.FHD   => "FHD (1024 px)",
            ImageQuality.QHD   => "2K  (1536 px)",
            ImageQuality.UHD4K => "4K  (2048 px)",
            _                  => value.ToString()
        } : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
