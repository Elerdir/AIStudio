using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Karta doporučeného base modelu v Trénovat tabu. Drží stav stažení (existující
/// na disku, právě stahuje, dostupný ke stažení) a delegate-based download akci.
///
/// <para>VM si sám nezavádí <see cref="Core.Interfaces.IDownloadService"/> — místo
/// toho dostane callback od parent <see cref="LoraTrainingPaneViewModel"/>, který
/// orchestruje download, refresh seznamu a auto-výběr po dokončení.</para>
/// </summary>
public partial class RecommendedBaseModelViewModel : ViewModelBase
{
    private readonly Func<RecommendedBaseModelViewModel, Task> _onDownloadRequested;
    private readonly Func<RecommendedBaseModelViewModel, Task> _onCancelRequested;

    public RecommendedModel Source { get; }

    /// <summary>Lidsky čitelný název pro UI hlavičku karty.</summary>
    public string Name => Source.Name;

    /// <summary>Krátký popis pro UI subtitle.</summary>
    public string Description => Source.Description;

    /// <summary>Velikost v MB / GB pro badge.</summary>
    public string SizeLabel => Source.SizeBytes switch
    {
        < 1024L * 1024 * 1024 => $"{Source.SizeBytes / (1024.0 * 1024):F0} MB",
        _                     => $"{Source.SizeBytes / (1024.0 * 1024 * 1024):F1} GB",
    };

    /// <summary>Typ modelu pro badge (SDXL / SD 1.5 / FLUX).</summary>
    public string ModelTypeLabel
    {
        get
        {
            var lower = Source.FileName.ToLowerInvariant();
            if (lower.Contains("flux"))                         return "FLUX";
            if (lower.Contains("xl") || lower.Contains("sdxl")) return "SDXL";
            return "SD 1.5";
        }
    }

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>True když je soubor už na disku.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(StateLabel))]
    private bool _isDownloaded;

    /// <summary>True dokud běží stahování (mezi klikem Stáhnout a dokončením).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(StateLabel))]
    private bool _isDownloading;

    /// <summary>0-100, jen za běhu downloadu.</summary>
    [ObservableProperty] private double _downloadProgress;

    /// <summary>Lidsky čitelný progress („1.2 GB / 6.7 GB · 45 MB/s").</summary>
    [ObservableProperty] private string _downloadStatusLine = string.Empty;

    /// <summary>True když lze stisknout Stáhnout (žádný probíhající DL, soubor chybí).</summary>
    public bool CanDownload => !IsDownloaded && !IsDownloading;

    /// <summary>„Staženo" / „Stahuji…" / null — pro chip badge na kartě.</summary>
    public string StateLabel => IsDownloaded
        ? "Staženo"
        : IsDownloading
            ? "Stahuji…"
            : string.Empty;

    /// <summary>Cesta, kam se model uloží po stažení — pro UI tooltip.</summary>
    public string TargetPath { get; }

    public RecommendedBaseModelViewModel(
        RecommendedModel source,
        string           targetPath,
        bool             isDownloaded,
        Func<RecommendedBaseModelViewModel, Task> onDownloadRequested,
        Func<RecommendedBaseModelViewModel, Task> onCancelRequested)
    {
        Source                = source;
        TargetPath            = targetPath;
        IsDownloaded          = isDownloaded;
        _onDownloadRequested  = onDownloadRequested;
        _onCancelRequested    = onCancelRequested;
    }

    [RelayCommand]
    private async Task DownloadAsync() => await _onDownloadRequested(this);

    [RelayCommand]
    private async Task CancelAsync() => await _onCancelRequested(this);
}
