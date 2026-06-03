using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace AIStudio.App.ViewModels.ImageStudio;

public partial class GeneratedImageViewModel : ObservableObject
{
    public string   Id        { get; init; } = Guid.NewGuid().ToString();
    public string   FilePath  { get; init; } = string.Empty;
    public string   Prompt    { get; init; } = string.Empty;
    public string   Model     { get; init; } = string.Empty;
    public long     Seed      { get; init; }
    public int      Width     { get; init; }
    public int      Height    { get; init; }
    public DateTime Timestamp { get; init; }
    public string   Sampler   { get; init; } = string.Empty;
    public string   Scheduler { get; init; } = string.Empty;
    public int      Steps     { get; init; }
    public double   Cfg       { get; init; }

    /// <summary>„image" / „video" — pro galerii (přehrávací ikonka, filtr typu).</summary>
    public string   MediaType { get; init; } = AIStudio.Core.Models.MediaTypes.Image;

    /// <summary>True, když jde o video (ne obrázek).</summary>
    public bool IsVideo => string.Equals(MediaType, AIStudio.Core.Models.MediaTypes.Video,
                                         StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private bool _isSelected;

    private Bitmap? _thumbnail;
    private bool    _thumbnailRequested;
    private Bitmap? _fullBitmap;

    /// <summary>
    /// 220 px náhled. Dekóduje se <b>asynchronně na pozadí</b> (ne na UI vlákně) — jinak
    /// se při materializaci mřížky galerie dekóduje desítky obrázků synchronně během
    /// renderu a aplikace „neodpovídá". Po načtení se doplní přes <see cref="OnPropertyChanged"/>.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        Bitmap? bmp;
        if (IsVideo)
        {
            // Video → vytáhni první snímek jako poster (vedle souboru, cachovaně).
            var thumbPath = FilePath + ".thumb.png";
            var ok = await Controls.VideoThumbnailGenerator.TryGenerateAsync(FilePath, thumbPath);
            bmp = ok ? await Task.Run(() => LoadFrom(thumbPath, 220)) : null;
        }
        else
        {
            bmp = await Task.Run(() => LoadFrom(FilePath, 220));
        }

        if (bmp is null) return;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            _thumbnail = bmp;
            OnPropertyChanged(nameof(Thumbnail));
        });
    }

    /// <summary>
    /// Plná velikost obrázku — pro hlavní canvas ostrý zobrazení.
    /// Lazy load, drží se v paměti dokud není instance VM uvolněná. Když máš
    /// stovky obrázků v galerii, je ~OK, protože plná verze se načítá pouze
    /// pro reálně otevřený VM (LatestImage / SelectedImage).
    /// </summary>
    public Bitmap? FullBitmap => _fullBitmap ??= LoadFullBitmap();

    public string ResolutionLabel      => $"{Width} × {Height}";
    public string TimeLabel            => Timestamp.ToString("HH:mm:ss");
    public string FullDateLabel        => Timestamp.ToString("d. M. yyyy HH:mm");
    public string SeedLabel            => Seed.ToString();
    public string FileName             => System.IO.Path.GetFileName(FilePath);
    public string StepsLabel           => $"Kroky: {Steps}";
    public string CfgLabel             => $"CFG: {Cfg:0.#}";
    public string SamplerSchedulerLabel => string.IsNullOrEmpty(Sampler) ? string.Empty
                                           : $"{Sampler} / {Scheduler}";
    public string ModelShort           => System.IO.Path.GetFileNameWithoutExtension(Model);

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CopyPromptAsync()
    {
        if (string.IsNullOrEmpty(Prompt)) return;
        await SetClipboardAsync(Prompt);
    }

    [RelayCommand]
    private async Task CopySeedAsync()
    {
        await SetClipboardAsync(Seed.ToString());
    }

    private static async Task SetClipboardAsync(string text)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;
        var clipboard = win.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (!File.Exists(FilePath)) return;
        // PlatformShell.Reveal() volá explorer /select na Win, open -R na macOS,
        // xdg-open container na Linuxu. Cross-platform replacement za nativní
        // Process.Start("explorer.exe").
        AIStudio.Infrastructure.Services.PlatformShell.Reveal(FilePath);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>Dekóduje obrázkový soubor na náhled dané šířky. Null při chybě.</summary>
    private static Bitmap? LoadFrom(string path, int width)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch { return null; }
    }

    private Bitmap? LoadFullBitmap()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var stream = File.OpenRead(FilePath);
            return new Bitmap(stream);
        }
        catch { return null; }
    }
}
