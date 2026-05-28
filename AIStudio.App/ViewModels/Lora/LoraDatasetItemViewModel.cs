using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Jeden obrázek + popisek v trénovacím datasetu LoRA. Zobrazený jako karta
/// v dataset drop zóně: thumbnail vlevo, název souboru, editovatelný caption,
/// tlačítko Odebrat.
///
/// <para>Caption se ukládá zvlášť — sd-scripts očekává <c>image.jpg + image.txt</c>
/// v dataset složce. AI Studio si soubory zkopíruje do working dir až při startu
/// tréninku, tady jen držíme cestu a text.</para>
/// </summary>
public partial class LoraDatasetItemViewModel : ViewModelBase
{
    private readonly Func<LoraDatasetItemViewModel, Task> _onRemoveRequested;

    /// <summary>Absolutní cesta k obrázku.</summary>
    public string ImagePath { get; }

    /// <summary>Jen filename pro UI label.</summary>
    public string FileName { get; }

    /// <summary>
    /// Popisek pro trénink. Auto-caption vyplní BLIP/WD-14, uživatel může editovat.
    /// Default = stub že caption chybí; doporučujeme uživateli napsat něco.
    /// </summary>
    [ObservableProperty] private string _caption = string.Empty;

    /// <summary>True když uživatel zatím nenapsal žádný caption — pro UI indikaci.</summary>
    public bool IsCaptionEmpty => string.IsNullOrWhiteSpace(Caption);

    /// <summary>True když právě běží auto-captioning na tomto obrázku.</summary>
    [ObservableProperty] private bool _isCaptioning;

    /// <summary>
    /// Thumbnail bitmap pro UI kartu (64×64 zmenšenina). Načítá se asynchronně
    /// po vytvoření instance, aby přidání 30 obrázků nezablokovalo UI thread
    /// při file pickeru / drag&drop.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    /// <summary>True když je thumbnail úspěšně načten — pro IsVisible v UI.</summary>
    public bool HasThumbnail => Thumbnail is not null;

    partial void OnCaptionChanged(string value) => OnPropertyChanged(nameof(IsCaptionEmpty));

    public LoraDatasetItemViewModel(string imagePath,
                                    Func<LoraDatasetItemViewModel, Task> onRemoveRequested)
    {
        ImagePath           = imagePath;
        FileName            = Path.GetFileName(imagePath);
        _onRemoveRequested  = onRemoveRequested;

        // Lazy thumbnail load — fire and forget. UI ukazuje placeholder ikonu
        // dokud Thumbnail dorazí; pak se přepne na obrázek.
        _ = LoadThumbnailAsync();
    }

    /// <summary>
    /// Načte thumbnail v background threadu (kvůli decode + downscale 64×64).
    /// Avalonia Bitmap.DecodeToWidth dělá disk read + decode na jednom kroku,
    /// takže nemusíme držet velký bitmap v paměti — jen zmenšený.
    /// </summary>
    private async Task LoadThumbnailAsync()
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(ImagePath)) return null;
                    using var stream = File.OpenRead(ImagePath);
                    // DecodeToWidth = decode na cílovou šířku (= šetří RAM i CPU).
                    // 128 px je dost na 64×64 retina kartičku v UI.
                    return Bitmap.DecodeToWidth(stream, 128);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "LoraDatasetItem: thumbnail decode selhal pro {Path}", ImagePath);
                    return null;
                }
            });

            if (bmp is not null)
                Dispatcher.UIThread.Post(() => Thumbnail = bmp);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraDatasetItem: thumbnail task selhala pro {Path}", ImagePath);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync() => await _onRemoveRequested(this);
}
