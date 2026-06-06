using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace AIStudio.App.ViewModels.Upscale;

/// <summary>
/// Jedna položka v upscale galerii — obrázek ze sledované složky, jeho náhled a stav
/// zvětšování. Náhled se načítá líně na pozadí (DecodeToWidth), ať se UI nezasekne u velké složky.
/// </summary>
public partial class UpscaleItemViewModel : ObservableObject
{
    public string FilePath { get; init; } = string.Empty;

    public string FileName => System.IO.Path.GetFileName(FilePath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>Probíhá zvětšování této položky.</summary>
    [ObservableProperty] private bool _isUpscaling;

    /// <summary>Krátký stav („Zvětšuji 45 %", „Hotovo", chyba).</summary>
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True po úspěšném zvětšení (UI ukáže ✓).</summary>
    [ObservableProperty] private bool _isDone;

    private bool _thumbRequested;

    /// <summary>Spustí líné načtení náhledu (volá se z UI při prvním zobrazení / scan).</summary>
    public void EnsureThumbnail()
    {
        if (_thumbRequested) return;
        _thumbRequested = true;
        _ = LoadThumbnailAsync();
    }

    private async Task LoadThumbnailAsync()
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var s = System.IO.File.OpenRead(FilePath);
                return Bitmap.DecodeToWidth(s, 220);
            });
            Dispatcher.UIThread.Post(() => Thumbnail = bmp);
        }
        catch (Exception ex)
        {
            Log.Debug("UpscaleItem: náhled {File} selhal: {Msg}", FileName, ex.Message);
        }
    }
}
