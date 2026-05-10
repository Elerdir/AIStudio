using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AIStudio.App.ViewModels.Models;

public enum ModelCategory { Chat, Image, Embedding }
public enum ModelSource { HuggingFace, Civitai, Local }

public partial class ModelItemViewModel : ViewModelBase
{
    // ── Static thumbnail cache (sdílená přes všechny instance) ───────────────

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new();
    private static readonly SemaphoreSlim _semaphore = new(6, 6);

    // ── Metadata ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatCategory), nameof(IsImageCategory), nameof(CategoryLabel))]
    private ModelCategory _category;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHfFilePicker), nameof(ShowDirectDownloadButton),
                              nameof(IsHuggingFace), nameof(IsCivitai))]
    private ModelSource _source;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVramInfo), nameof(VramLabel))]
    private int _vramRequiredGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextLength))]
    private int _contextLength;

    [ObservableProperty] private string _quantization = string.Empty;

    /// <summary>Název souboru, pod kterým se model uloží do složky Models.</summary>
    [ObservableProperty] private string _fileName = string.Empty;

    /// <summary>Přímé URL pro stažení (HuggingFace resolve/main nebo Civitai api/download).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDirectDownloadButton))]
    private string _downloadUrl = string.Empty;

    /// <summary>URL stránky modelu na HuggingFace nebo Civitai (pro zobrazení v prohlížeči).</summary>
    [ObservableProperty] private string _modelPageUrl = string.Empty;

    /// <summary>Velikost souboru jako text (např. "4.7 GB") — slouží jen pro zobrazení v UI.</summary>
    [ObservableProperty] private string _size = string.Empty;

    /// <summary>
    /// Reference na model v původním zdroji — pro HF "owner/repo", pro Civitai číselné ID.
    /// Používá se pro pozdější dohledání (např. nahrání souborů z HF tree po výběru).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHfFilePicker), nameof(ShowDirectDownloadButton))]
    private string _providerRef = string.Empty;

    /// <summary>URL náhledového obrázku (Civitai) — pro thumb v UI.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private string _thumbnailUrl = string.Empty;

    /// <summary>Načtený bitmap náhledu — null dokud se nestáhne z ThumbnailUrl.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadedThumbnail))]
    private Bitmap? _thumbnailBitmap;

    /// <summary>Civitai base model label, např. "SDXL 1.0", "Pony", "Flux.1 D".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBaseModel))]
    private string _baseModel = string.Empty;

    /// <summary>True pokud Civitai označil model jako NSFW. Pro UI badge.</summary>
    [ObservableProperty] private bool _isNsfw;

    /// <summary>Počet stažení (HF + Civitai). Pro popisek pod modelem.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadCountLabel))]
    private long _downloadCount;

    /// <summary>Civitai rating 0-5 (0 = N/A).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRating), nameof(RatingLabel))]
    private double _rating;

    public bool HasThumbnail       => !string.IsNullOrEmpty(ThumbnailUrl);
    public bool HasLoadedThumbnail => ThumbnailBitmap is not null;
    public bool HasBaseModel    => !string.IsNullOrEmpty(BaseModel);
    public bool HasRating       => Rating > 0;
    public string RatingLabel   => Rating > 0 ? $"{Rating:F1}" : "";
    public bool HasVramInfo     => VramRequiredGb > 0;
    public string VramLabel     => VramRequiredGb > 0 ? $"~{VramRequiredGb} GB VRAM" : "";
    public bool IsChatCategory  => Category == ModelCategory.Chat;
    public bool IsImageCategory => Category == ModelCategory.Image;

    /// <summary>True pro HF položku — v detailu se ukáže seznam GGUF souborů (kvantizací).</summary>
    public bool IsHuggingFace      => Source == ModelSource.HuggingFace;
    /// <summary>True pro Civitai položku — má přímý DownloadUrl, není co rozbalovat.</summary>
    public bool IsCivitai          => Source == ModelSource.Civitai;
    /// <summary>HF položka s naplněným ProviderRef — ukázat list dostupných variant.</summary>
    public bool ShowHfFilePicker   => IsHuggingFace && !string.IsNullOrEmpty(ProviderRef);
    /// <summary>Položky s přímou DownloadUrl (Civitai, HF po výběru souboru, lokální import).</summary>
    public bool ShowDirectDownloadButton =>
        ShowDownloadButton && !string.IsNullOrEmpty(DownloadUrl);

    public string DownloadCountLabel => DownloadCount switch
    {
        >= 1_000_000 => $"{DownloadCount / 1_000_000.0:F1}M",
        >= 1_000     => $"{DownloadCount / 1_000.0:F1}k",
        _            => DownloadCount.ToString()
    };

    // ── Stav modelu ──────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloaded), nameof(ShowDownloadButton))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloaded), nameof(ShowDownloadButton),
                              nameof(ShowDirectDownloadButton))]
    private bool _isDownloaded;

    [ObservableProperty] private bool _isSelected;

    // ── Stav stahování ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloading), nameof(ShowDownloadButton),
                              nameof(ShowDownloaded), nameof(ShowDownloadError),
                              nameof(ShowDirectDownloadButton))]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadedLabel))]
    private long _downloadedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadedLabel), nameof(DownloadProgressLabel))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadProgressLabel))]
    private double _downloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSpeedLabel), nameof(EtaLabel))]
    private double _downloadSpeedBytesPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowDownloadError),
                              nameof(ShowDownloadButton), nameof(ShowDownloaded),
                              nameof(ShowDirectDownloadButton))]
    private string _downloadError = string.Empty;

    /// <summary>Skutečná velikost souboru na disku — nastaví se při skenování složky.</summary>
    [ObservableProperty] private string _sizeOnDisk = string.Empty;

    /// <summary>SHA-256 hash pro ověření integrity po stažení — jen pro Civitai, null pro HF/lokální.</summary>
    public string? Sha256 { get; init; }

    /// <summary>True po dokončení downloadu, dokud probíhá SHA-256 checksum verifikace.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloading), nameof(DownloadProgressLabel))]
    private bool _isVerifyingChecksum;

    /// <summary>
    /// True pokud uživatel klikl na „Odebrat model" a teď čekáme na potvrzení.
    /// Při true XAML zobrazuje místo tlačítka „Odebrat" inline panel
    /// „Opravdu smazat? [Ano] [Zrušit]" — prevence překlepnutí bez modálního dialogu.
    /// </summary>
    [ObservableProperty] private bool _isConfirmingDelete;

    /// <summary>První klik na „Odebrat" — zapne confirm panel.</summary>
    [RelayCommand]
    private void BeginDeleteConfirmation() => IsConfirmingDelete = true;

    /// <summary>Klik na „Zrušit" v confirm panelu — vypne ho.</summary>
    [RelayCommand]
    private void CancelDeleteConfirmation() => IsConfirmingDelete = false;

    // ── Computed: viditelnost stavů ───────────────────────────────────────────
    public bool ShowDownloading    => IsDownloading || IsVerifyingChecksum;
    public bool ShowDownloadError  => !IsDownloading && HasError;
    public bool ShowDownloaded     => IsDownloaded && !IsDownloading && !HasError;
    public bool ShowDownloadButton => !IsDownloaded && !IsDownloading && !HasError;
    public bool HasError           => !string.IsNullOrEmpty(DownloadError);
    public bool HasContextLength   => ContextLength > 0;

    // ── Computed: formátovaný stav stahování ─────────────────────────────────
    public string DownloadProgressLabel =>
        IsVerifyingChecksum ? "Ověřuji…" :
        TotalBytes > 0 ? $"{DownloadProgress:F0} %" : "…";

    public string DownloadedLabel =>
        TotalBytes > 0
            ? $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}"
            : FormatBytes(DownloadedBytes);

    public string DownloadSpeedLabel =>
        DownloadSpeedBytesPerSec < 1
            ? ""
            : DownloadSpeedBytesPerSec < 1_048_576
                ? $"{DownloadSpeedBytesPerSec / 1024.0:F0} KB/s"
                : $"{DownloadSpeedBytesPerSec / 1_048_576.0:F1} MB/s";

    /// <summary>Odhadovaný zbývající čas stahování.</summary>
    public string EtaLabel
    {
        get
        {
            if (!IsDownloading || DownloadSpeedBytesPerSec < 1 || TotalBytes <= 0) return "";
            var remaining = TotalBytes - DownloadedBytes;
            if (remaining <= 0) return "";
            var seconds = remaining / DownloadSpeedBytesPerSec;
            if (seconds < 60)   return $"~{(int)seconds} s";
            if (seconds < 3600) return $"~{(int)(seconds / 60)} min";
            return $"~{seconds / 3600:F1} h";
        }
    }

    // ── Computed: popisky ─────────────────────────────────────────────────────
    public string CategoryLabel => Category switch
    {
        ModelCategory.Chat      => "Chat",
        ModelCategory.Image     => "Obrázky",
        ModelCategory.Embedding => "Embedding",
        _ => string.Empty
    };

    public string SourceLabel => Source switch
    {
        ModelSource.HuggingFace => "HuggingFace",
        ModelSource.Civitai     => "Civitai",
        ModelSource.Local       => "Lokální",
        _ => string.Empty
    };

    // ── Thumbnail async loading ───────────────────────────────────────────────

    partial void OnThumbnailUrlChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        // Z cache vrátíme okamžitě, jinak spustíme stahování na pozadí
        if (_cache.TryGetValue(value, out var cached))
        {
            ThumbnailBitmap = cached;
            return;
        }

        _ = FetchThumbnailAsync(value);
    }

    private async Task FetchThumbnailAsync(string url)
    {
        await _semaphore.WaitAsync();
        try
        {
            // Druhá kontrola cache po získání semaforu (jiný task mohl mezitím načíst)
            if (_cache.TryGetValue(url, out var cached))
            {
                Dispatcher.UIThread.Post(() => ThumbnailBitmap = cached);
                return;
            }

            var bytes  = await _http.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            _cache[url] = bitmap;
            Dispatcher.UIThread.Post(() => ThumbnailBitmap = bitmap);
        }
        catch (Exception ex)
        {
            _cache[url] = null; // označíme jako selhané, příště neopakujeme
            Log.Debug("Thumbnail load failed for {Url}: {Msg}", url, ex.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Helper ────────────────────────────────────────────────────────────────
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1_024                     => $"{bytes} B",
        < 1_048_576                 => $"{bytes / 1_024.0:F0} KB",
        < 1_073_741_824             => $"{bytes / 1_048_576.0:F1} MB",
        _                           => $"{bytes / 1_073_741_824.0:F2} GB"
    };
}
