using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// ViewModel pro záložku „Trénovat" v LoRA stránce. Drží stav datasetu,
/// parametry, base model, progress během tréninku.
///
/// <para>Závislosti:</para>
/// <list type="bullet">
/// <item><see cref="ILoraTrainerService"/> — spustí Python trénink</item>
/// <item><see cref="ILoraTrainerDependencyService"/> — info o chybějících balíčcích</item>
/// <item><see cref="ISettingsService"/> — Models adresář pro scan checkpointů + output dir</item>
/// <item><see cref="ISystemMonitorService"/> — VRAM detekce pro HW indikátor</item>
/// </list>
///
/// <para>Workflow: uživatel zadá název, vybere base model z dropdownu (lokálních
/// checkpointů), přetáhne fotky do drop zóny, doupraví captiony, klikne Spustit.
/// Trénink běží jako Task na pozadí; UI se updatuje přes <see cref="IProgress{T}"/>.</para>
/// </summary>
public partial class LoraTrainingPaneViewModel : ViewModelBase
{
    private readonly ILoraTrainerService           _trainer;
    private readonly ILoraTrainerDependencyService _deps;
    private readonly ISettingsService              _settings;
    private readonly ISystemMonitorService?        _monitor;
    private readonly ILoraCaptionService?          _captionService;
    private readonly IDownloadService?             _downloadService;
    // FLUX trénink potřebuje clip_l/t5/ae — zajistíme je stejnou službou jako
    // FLUX generování. Null = degradace (FLUX deps se musí stáhnout jinde).
    private readonly IFluxDependencyService?       _fluxDeps;
    private readonly IComfyService?                 _comfy;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _captionCts;
    private readonly Dictionary<string, CancellationTokenSource> _modelDownloadCts = new();

    // ── Konfigurace tréninku ──────────────────────────────────────────────────

    /// <summary>Název výstupního souboru (bez .safetensors). Validujeme při Start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartTraining))]
    private string _trainingName = string.Empty;

    /// <summary>Vybraný base model (.safetensors). Plněno scanem ModelsDirectory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartTraining), nameof(BaseModelTypeLabel),
                              nameof(EstimatedTimeLabel))]
    private string? _selectedBaseModel;

    /// <summary>
    /// Seznam dostupných base modelů — display name (s prefixem zdroje, např.
    /// „[ComfyUI] sd_xl_base_1.0.safetensors"). Mapování na absolutní cestu
    /// drží <see cref="_baseModelPaths"/>.
    /// </summary>
    public ObservableCollection<string> AvailableBaseModels { get; } = new();

    /// <summary>Mapování display name → absolutní cesta na disku. Vyplňuje RefreshBaseModelsAsync.</summary>
    private readonly Dictionary<string, string> _baseModelPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True když máme aspoň jeden checkpoint — pro UI empty state.</summary>
    public bool HasAvailableBaseModels => AvailableBaseModels.Count > 0;

    /// <summary>Lidsky čitelná detekce typu modelu (SDXL / SD 1.5 / FLUX) — pro UI badge.</summary>
    public string BaseModelTypeLabel
    {
        get
        {
            if (string.IsNullOrEmpty(SelectedBaseModel)) return string.Empty;
            // SelectedBaseModel může mít prefix „[ComfyUI] " nebo „[Vlastní] " —
            // pro detekci typu nás zajímá jen filename.
            var path  = ResolveSelectedBaseModelPath() ?? SelectedBaseModel;
            var lower = Path.GetFileName(path).ToLowerInvariant();
            if (lower.Contains("flux"))                                return "FLUX";
            if (lower.Contains("xl") || lower.Contains("sdxl"))        return "SDXL";
            return "SD 1.5";
        }
    }

    // ── Dataset ───────────────────────────────────────────────────────────────

    /// <summary>Obrázky v datasetu (15-30 doporučeno).</summary>
    public ObservableCollection<LoraDatasetItemViewModel> DatasetItems { get; } = new();

    /// <summary>True když ještě nejsou žádné obrázky — pro empty state v drop zóně.</summary>
    public bool HasNoDatasetItems => DatasetItems.Count == 0;

    /// <summary>Lidsky čitelný status datasetu („18 obrázků · 4 bez popisku").</summary>
    public string DatasetStatusLabel
    {
        get
        {
            if (DatasetItems.Count == 0) return "Žádné obrázky";
            var noCaption = DatasetItems.Count(i => i.IsCaptionEmpty);
            var label     = $"{DatasetItems.Count} obrázků";
            if (noCaption > 0) label += $" · {noCaption} bez popisku";
            return label;
        }
    }

    /// <summary>True když počet obrázků je mimo doporučený interval (15-30).</summary>
    public bool IsDatasetSizeWarning =>
        DatasetItems.Count > 0 && (DatasetItems.Count < 15 || DatasetItems.Count > 50);

    /// <summary>Tooltip pro varování o velikosti datasetu.</summary>
    public string DatasetSizeWarningText => DatasetItems.Count switch
    {
        < 5  => $"Příliš málo obrázků ({DatasetItems.Count}). Minimum je 5, doporučeno 15-30.",
        < 15 => $"Málo obrázků ({DatasetItems.Count}). LoRA může být nedotrénovaná. Doporučeno 15-30.",
        > 50 => $"Hodně obrázků ({DatasetItems.Count}). Trénink potrvá dlouho. Doporučeno do 30.",
        _    => string.Empty,
    };


    // ── Stav tréninku ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartTraining), nameof(IsIdle), nameof(IsInitializing),
                              nameof(CanStartCaptioning))]
    private bool _isTraining;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitializing))]
    private int    _currentStep;

    [ObservableProperty] private int    _totalSteps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentLabel))]
    private double _currentProgress;       // 0-100

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitializing))]
    private string _statusLine = string.Empty;

    [ObservableProperty] private string _elapsedLabel = string.Empty;
    [ObservableProperty] private string _remainingLabel = string.Empty;
    [ObservableProperty] private string _lossLabel = string.Empty;

    /// <summary>Procenta jako label pro UI („42 %"). Při 0 ukáže „0 %".</summary>
    public string ProgressPercentLabel => $"{CurrentProgress:F0} %";

    /// <summary>
    /// True dokud trénink běží, ale ještě nezačal tikat kroky (Krok 0 / setup).
    /// V této fázi se načítá checkpoint do VRAM (~30-60 s) — UI ukáže „Inicializuji…"
    /// místo statického „Krok 0", aby uživatel věděl, že se něco děje.
    /// </summary>
    public bool IsInitializing => IsTraining && CurrentStep <= 0;

    /// <summary>True když nic neběží — UI ukáže formulář.</summary>
    public bool IsIdle => !IsTraining;

    /// <summary>True když je trénink připraven ke startu (všechna pole vyplněná).</summary>
    public bool CanStartTraining =>
        !IsTraining &&
        !IsCaptioning &&
        !string.IsNullOrWhiteSpace(TrainingName) &&
        !string.IsNullOrEmpty(SelectedBaseModel) &&
        DatasetItems.Count >= 5;

    [ObservableProperty] private string _resultMessage = string.Empty;
    [ObservableProperty] private bool   _isResultSuccess;
    [ObservableProperty] private bool   _isResultError;

    // ── Konstruktor + inicializace ────────────────────────────────────────────

    public LoraTrainingPaneViewModel(
        ILoraTrainerService            trainer,
        ILoraTrainerDependencyService  deps,
        ISettingsService               settings,
        ISystemMonitorService?         monitor         = null,
        ILoraCaptionService?           captionService  = null,
        IDownloadService?              downloadService = null,
        IFluxDependencyService?        fluxDeps        = null,
        IComfyService?                 comfy           = null)
    {
        AvailableBaseModels.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasAvailableBaseModels));

        _trainer         = trainer;
        _deps            = deps;
        _settings        = settings;
        _monitor         = monitor;
        _captionService  = captionService;
        _downloadService = downloadService;
        _fluxDeps        = fluxDeps;
        _comfy           = comfy;

        // SystemMonitor sbírá metriky každé 2.5 s, takže Current je při startu
        // null (ještě nestihl odběr). DetectHardware si subscribneme i na
        // StatusUpdated event — po prvním samplu se HW labels naplní.
        // Zároveň okamžitě uděláme jeden pokus pro případ že už nějaký
        // sample existuje (např. ChatPageViewModel ho už triggernul).
        if (_monitor is not null)
            _monitor.StatusUpdated += OnSystemStatusUpdated;

        DatasetItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoDatasetItems));
            OnPropertyChanged(nameof(DatasetStatusLabel));
            OnPropertyChanged(nameof(IsDatasetSizeWarning));
            OnPropertyChanged(nameof(DatasetSizeWarningText));
            OnPropertyChanged(nameof(CanStartTraining));
            // BUG fix: bez tohoto zůstalo tlačítko Auto-popisek disabled —
            // CanStartCaptioning se po přidání fotek nikdy nepřepočítal a
            // držel initial false (0 fotek při startu).
            OnPropertyChanged(nameof(CanStartCaptioning));
            // Přepočítej doporučený počet kroků podle počtu fotek (~100/fotku).
            UpdateRecommendedSteps();
        };

        DetectHardware();
        BuildRecommendedBaseModels();
        _ = RefreshBaseModelsAsync();
    }

}
