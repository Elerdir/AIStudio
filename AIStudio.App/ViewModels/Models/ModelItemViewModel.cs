using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIStudio.App.ViewModels.Models;

public enum ModelCategory { Chat, Image, Embedding }
public enum ModelSource { HuggingFace, Civitai, Local }

public partial class ModelItemViewModel : ViewModelBase
{
    // ── Metadata ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private ModelCategory _category;
    [ObservableProperty] private ModelSource _source;
    [ObservableProperty] private int _vramRequiredGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextLength))]
    private int _contextLength;

    [ObservableProperty] private string _quantization = string.Empty;

    /// <summary>Název souboru, pod kterým se model uloží do složky Models.</summary>
    [ObservableProperty] private string _fileName = string.Empty;

    /// <summary>Přímé URL pro stažení (HuggingFace resolve/main nebo Civitai api/download).</summary>
    [ObservableProperty] private string _downloadUrl = string.Empty;

    /// <summary>URL stránky modelu na HuggingFace nebo Civitai (pro zobrazení v prohlížeči).</summary>
    [ObservableProperty] private string _modelPageUrl = string.Empty;

    /// <summary>Velikost souboru jako text (např. "4.7 GB") — slouží jen pro zobrazení v UI.</summary>
    [ObservableProperty] private string _size = string.Empty;

    // ── Stav modelu ──────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloaded), nameof(ShowDownloadButton))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloaded), nameof(ShowDownloadButton))]
    private bool _isDownloaded;

    [ObservableProperty] private bool _isSelected;

    // ── Stav stahování ───────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloading), nameof(ShowDownloadButton), nameof(ShowDownloaded), nameof(ShowDownloadError))]
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
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowDownloadError), nameof(ShowDownloadButton), nameof(ShowDownloaded))]
    private string _downloadError = string.Empty;

    /// <summary>Skutečná velikost souboru na disku — nastaví se při skenování složky.</summary>
    [ObservableProperty] private string _sizeOnDisk = string.Empty;

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
    public bool ShowDownloading    => IsDownloading;
    public bool ShowDownloadError  => !IsDownloading && HasError;
    public bool ShowDownloaded     => IsDownloaded && !IsDownloading && !HasError;
    public bool ShowDownloadButton => !IsDownloaded && !IsDownloading && !HasError;
    public bool HasError           => !string.IsNullOrEmpty(DownloadError);
    public bool HasContextLength   => ContextLength > 0;

    // ── Computed: formátovaný stav stahování ─────────────────────────────────
    public string DownloadProgressLabel =>
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

    // ── Helper ────────────────────────────────────────────────────────────────
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1_024                     => $"{bytes} B",
        < 1_048_576                 => $"{bytes / 1_024.0:F0} KB",
        < 1_073_741_824             => $"{bytes / 1_048_576.0:F1} MB",
        _                           => $"{bytes / 1_073_741_824.0:F2} GB"
    };
}
