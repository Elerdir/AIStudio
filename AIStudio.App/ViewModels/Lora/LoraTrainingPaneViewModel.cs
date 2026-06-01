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

    // ── Parametry ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedTimeLabel))]
    private int _rank = 32;

    [ObservableProperty] private int _alpha = 16;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedTimeLabel))]
    private int _steps = 1500;

    [ObservableProperty] private double _learningRate = 1e-4;

    [ObservableProperty] private int _batchSize = 1;

    [ObservableProperty] private string _selectedOptimizer = "AdamW8bit";

    public IReadOnlyList<string> AvailableOptimizers { get; } =
        new[] { "AdamW8bit", "AdamW", "Lion", "Prodigy" };

    /// <summary>Aktivní preset pro zvýraznění tlačítka (null = vlastní/ručně upraveno).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresetPerson), nameof(IsPresetStyle), nameof(IsPresetQuick))]
    private string? _activePreset = "person";

    public bool IsPresetPerson => ActivePreset == "person";
    public bool IsPresetStyle  => ActivePreset == "style";
    public bool IsPresetQuick  => ActivePreset == "quick";

    /// <summary>
    /// Presety parametrů jedním klikem. Hodnoty osvědčené pro SDXL na 8+ GB VRAM:
    /// <list type="bullet">
    /// <item><b>Postava</b> — konkrétní osoba/obličej. Rank 32, 1500 kroků,
    ///   batch 2 (využije VRAM 3090), 1e-4. Sweet spot pro ~20-30 fotek.</item>
    /// <item><b>Styl</b> — estetika/umělecký styl. Vyšší rank 64 (víc kapacity
    ///   pro komplexní vzory), 2000 kroků, nižší LR 5e-5 (jemnější).</item>
    /// <item><b>Rychlý test</b> — ověření že pipeline funguje. Rank 16, jen 400
    ///   kroků, ~2-3 min. Výsledek je hrubý, ale rychle uvidíš, zda to jede.</item>
    /// </list>
    /// </summary>
    [RelayCommand]
    private void ApplyPreset(string preset)
    {
        // Guard: nastavování hodnot níže by jinak přes OnXxxChanged shodilo
        // ActivePreset zpátky na null (vypadalo by to jako „ruční úprava").
        _applyingPreset = true;
        try
        {
            switch (preset)
            {
                case "person":
                    Rank = 32; Alpha = 16; Steps = 1500; LearningRate = 1e-4;
                    BatchSize = RecommendedBatchForVram(); SelectedOptimizer = "AdamW8bit";
                    break;
                case "style":
                    Rank = 64; Alpha = 32; Steps = 2000; LearningRate = 5e-5;
                    BatchSize = RecommendedBatchForVram(); SelectedOptimizer = "AdamW8bit";
                    break;
                case "quick":
                    Rank = 16; Alpha = 8; Steps = 400; LearningRate = 1e-4;
                    BatchSize = 1; SelectedOptimizer = "AdamW8bit";
                    break;
                default:
                    return;
            }
            ActivePreset = preset;
        }
        finally { _applyingPreset = false; }
    }

    /// <summary>
    /// Doporučený batch size podle VRAM. 24 GB → 2 (rychlejší, gradient hladší
    /// u SDXL LoRA), pod 12 GB → 1 (jistota proti OOM).
    /// </summary>
    private int RecommendedBatchForVram()
    {
        var vram = _monitor?.Current?.VramTotalGb ?? 0;
        return vram >= 16 ? 2 : 1;
    }

    // Když uživatel ručně sáhne na parametr, zrušíme zvýraznění presetu
    // (hodnota už neodpovídá presetu — je „vlastní").
    partial void OnRankChanged(int value)            => ClearPresetIfManual();
    partial void OnAlphaChanged(int value)           => ClearPresetIfManual();
    partial void OnStepsChanged(int value)
    {
        ClearPresetIfManual();
        // Ruční editace kroků zruší auto-doporučení (aby přidání další fotky
        // uživateli nepřepsalo jeho vlastní hodnotu).
        if (!_autoSettingSteps) _userTouchedSteps = true;
    }

    /// <summary>Poslední naměřená VRAM (GB) z monitoru — cache pro chvíle, kdy je Current dočasně 0.</summary>
    private double _lastKnownVramGb;
    /// <summary>True když uživatel ručně přepsal počet kroků — pak ho auto-doporučení nepřepisuje.</summary>
    private bool _userTouchedSteps;
    /// <summary>Guard: programové nastavení Steps nemá počítat jako ruční editaci.</summary>
    private bool _autoSettingSteps;

    /// <summary>
    /// Doporučený počet kroků = ~100 na fotku (osvědčená heuristika pro person/character
    /// LoRA), ohraničený doporučeným minimem dle base modelu a stropem 4000. Aplikuje se
    /// jen dokud uživatel kroky ručně nepřepsal.
    /// </summary>
    private void UpdateRecommendedSteps()
    {
        if (_userTouchedSteps || IsTraining) return;

        var filename = Path.GetFileName(ResolveSelectedBaseModelPath() ?? SelectedBaseModel ?? string.Empty);
        var baseSteps = LoraTrainingParameters.DefaultsFor(filename).Steps;
        var recommended = DatasetItems.Count > 0
            ? Math.Clamp(DatasetItems.Count * 100, baseSteps, 4000)
            : baseSteps;

        _autoSettingSteps = true;
        Steps = recommended;
        _autoSettingSteps = false;
    }
    partial void OnLearningRateChanged(double value) => ClearPresetIfManual();
    partial void OnBatchSizeChanged(int value)       => ClearPresetIfManual();
    partial void OnSelectedOptimizerChanged(string value) => ClearPresetIfManual();

    /// <summary>Guard proti rekurzi: ApplyPreset nastavuje hodnoty, nechceme aby to hned shodilo preset.</summary>
    private bool _applyingPreset;

    private void ClearPresetIfManual()
    {
        if (!_applyingPreset) ActivePreset = null;
    }

    // ── HW indikátor + odhad ──────────────────────────────────────────────────

    [ObservableProperty] private string _hwInfoLabel = "Detekuji GPU…";
    [ObservableProperty] private bool   _hwSupportsTraining = true;
    [ObservableProperty] private string _hwWarningText = string.Empty;

    /// <summary>Odhad doby tréninku podle rank/steps/VRAM. Pro UX pomocný číselný indikátor.</summary>
    public string EstimatedTimeLabel
    {
        get
        {
            if (string.IsNullOrEmpty(SelectedBaseModel)) return string.Empty;
            // Hrubý odhad: ~3 it/s na RTX 3090 pro SDXL @ rank 32 batch 1 → ~500 s na 1500 stepů.
            // Škálujeme rank lineárně, batch lineárně, model typu (SDXL ~1.0, SD1.5 ~0.4, FLUX ~2.0).
            var modelFactor = BaseModelTypeLabel switch
            {
                "FLUX"   => 2.0,
                "SDXL"   => 1.0,
                _        => 0.4,
            };
            var rankFactor = Rank / 32.0;
            var stepsPerSec = 3.0 / modelFactor / Math.Max(0.6, rankFactor);
            var seconds = Steps / Math.Max(0.5, stepsPerSec);

            // Slabší HW (pod 12 GB VRAM) — 1.5-2× pomalejší
            if (_monitor?.Current?.VramTotalGb < 12) seconds *= 1.8;

            return seconds switch
            {
                < 60   => $"~{seconds:F0} s",
                < 3600 => $"~{seconds / 60:F0} min",
                _      => $"~{seconds / 3600:F1} h",
            };
        }
    }

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
        IFluxDependencyService?        fluxDeps        = null)
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

    // ── Doporučené base modely (s download akcí) ──────────────────────────────

    /// <summary>
    /// Curated seznam doporučených SDXL checkpointů pro trénink — jednorázově
    /// při startu naplníme z <see cref="RecommendedModels"/>. State (downloaded/
    /// downloading) se refreshuje při změně Available list (RefreshBaseModelsAsync).
    /// </summary>
    public ObservableCollection<RecommendedBaseModelViewModel> RecommendedBaseModels { get; } = new();

    /// <summary>True když máme funkční download service v DI — pro UI IsVisible.</summary>
    public bool IsDownloadSupported => _downloadService is not null;

    private void BuildRecommendedBaseModels()
    {
        // Curated picky vhodné pro LoRA trénink. Sdílíme katalog s chat → image gen
        // recommenderem (RecommendedModels.*), takže přidání modelu do katalogu se
        // automaticky promítne sem.
        // Nabízíme jen TRÉNOVATELNÉ base modely (plný safetensors). FLUX GGUF
        // (netrénovatelný) tu není — pro FLUX je tu fp8 dev safetensors. Gate
        // na GGUF je ve SdScriptsLoraTrainer.ValidateRequest.
        var picks = new[]
        {
            RecommendedModels.SdxlBase10,             // univerzální SDXL pro postavy/scény
            RecommendedModels.DreamShaperXl_Lightning, // stylizovaný/cinematic SDXL
            RecommendedModels.AnimagineXl31,          // anime SDXL
            RecommendedModels.DreamShaper8_Sd15,      // SD 1.5 — rychlejší, míň VRAM
            RecommendedModels.FluxDev_Fp8,            // FLUX dev fp8 — nejvyšší kvalita
        };

        var modelsRoot = AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
        var ckptDir    = Path.Combine(modelsRoot, "checkpoints");

        foreach (var p in picks)
        {
            var targetPath = Path.Combine(ckptDir, p.FileName);
            var existing   = File.Exists(targetPath) || IsFileInComfyUiCheckpoints(p.FileName);
            RecommendedBaseModels.Add(new RecommendedBaseModelViewModel(
                source:              p,
                targetPath:          targetPath,
                isDownloaded:        existing,
                onDownloadRequested: OnRecommendedDownloadRequestedAsync,
                onCancelRequested:   OnRecommendedDownloadCancelAsync));
        }
    }

    /// <summary>Refreshne flag IsDownloaded u doporučených podle aktuálně přítomných checkpointů.</summary>
    private void RefreshRecommendedDownloadedFlags()
    {
        foreach (var rec in RecommendedBaseModels)
        {
            if (rec.IsDownloading) continue;   // neměníme stav v průběhu DL
            rec.IsDownloaded = File.Exists(rec.TargetPath) || IsFileInComfyUiCheckpoints(rec.Source.FileName);
        }
    }

    /// <summary>True pokud daný filename existuje v ComfyUI bundle checkpoints/.</summary>
    private bool IsFileInComfyUiCheckpoints(string fileName)
    {
        var comfyDir = _settings.Settings.ComfyUiDirectory;
        if (string.IsNullOrWhiteSpace(comfyDir)) return false;
        return File.Exists(Path.Combine(comfyDir, "models", "checkpoints", fileName));
    }

    private async Task OnRecommendedDownloadRequestedAsync(RecommendedBaseModelViewModel rec)
    {
        if (_downloadService is null) return;
        if (rec.IsDownloading || rec.IsDownloaded) return;

        var cts = new CancellationTokenSource();
        _modelDownloadCts[rec.Source.Id] = cts;

        rec.IsDownloading      = true;
        rec.DownloadProgress   = 0;
        rec.DownloadStatusLine = "Spouštím stahování…";

        var progress = new Progress<DownloadProgressInfo>(p => Dispatcher.UIThread.Post(() =>
        {
            rec.DownloadProgress   = p.Percent;
            var mbps               = p.BytesPerSecond / 1_048_576;
            rec.DownloadStatusLine = p.Total > 0
                ? $"{p.Downloaded / 1_048_576} / {p.Total / 1_048_576} MB · {mbps:F1} MB/s"
                : $"{p.Downloaded / 1_048_576} MB · {mbps:F1} MB/s";
        }));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rec.TargetPath)!);

            var token = rec.Source.RequiresHuggingFaceToken
                ? _settings.Settings.HuggingFaceToken
                : null;

            await _downloadService.DownloadFileAsync(
                rec.Source.DownloadUrl,
                rec.TargetPath,
                progress,
                token,
                cts.Token,
                rec.Source.Sha256);

            Dispatcher.UIThread.Post(() =>
            {
                rec.IsDownloading      = false;
                rec.IsDownloaded       = true;
                rec.DownloadProgress   = 100;
                rec.DownloadStatusLine = "Hotovo";
            });

            // Refresh seznamu Available aby se nový model objevil v dropdownu;
            // a pokud nic není vybrané, auto-select tenhle.
            await RefreshBaseModelsAsync();

            Dispatcher.UIThread.Post(() =>
            {
                if (string.IsNullOrEmpty(SelectedBaseModel))
                {
                    var match = AvailableBaseModels
                        .FirstOrDefault(n => n.Contains(rec.Source.FileName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) SelectedBaseModel = match;
                }
            });

            Log.Information("LoraTrainingPane: model {Name} stažen do {Path}",
                rec.Source.Name, rec.TargetPath);
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                rec.IsDownloading      = false;
                rec.DownloadStatusLine = "Zrušeno";
            });
            try { if (File.Exists(rec.TargetPath)) File.Delete(rec.TargetPath); } catch { }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraTrainingPane: stažení {Name} selhalo", rec.Source.Name);
            Dispatcher.UIThread.Post(() =>
            {
                rec.IsDownloading      = false;
                rec.DownloadStatusLine = $"❌ {ex.Message}";
            });
        }
        finally
        {
            _modelDownloadCts.Remove(rec.Source.Id);
            cts.Dispose();
        }
    }

    private Task OnRecommendedDownloadCancelAsync(RecommendedBaseModelViewModel rec)
    {
        if (_modelDownloadCts.TryGetValue(rec.Source.Id, out var cts))
        {
            try { cts.Cancel(); }
            catch (Exception ex) { Log.Warning(ex, "LoraTrainingPane: cancel DL selhal"); }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Otevře <c>Models/checkpoints/</c> v default file manageru — uživatel
    /// si tam může ručně zkopírovat stažený .safetensors checkpoint.
    /// Pokud složka neexistuje, vytvoříme ji.
    /// </summary>
    [RelayCommand]
    private void OpenCheckpointsFolder()
    {
        var modelsRoot = AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
        var ckptDir    = Path.Combine(modelsRoot, "checkpoints");
        try
        {
            if (!Directory.Exists(ckptDir)) Directory.CreateDirectory(ckptDir);
            AIStudio.Infrastructure.Services.PlatformShell.Open(ckptDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraTrainingPane: otevření {Dir} selhalo", ckptDir);
        }
    }

    /// <summary>
    /// Naskenuje <b>všechny známé checkpoint lokace</b> a naplní seznam base modelů:
    /// <list type="bullet">
    /// <item>AI Studio Models/checkpoints/ (settings.ModelsDirectory)</item>
    /// <item>ComfyUI bundle models/checkpoints/ (settings.ComfyUiDirectory)</item>
    /// </list>
    /// Důvod: ComfyUI Portable má vlastní složku <c>models/checkpoints/</c> a uživatel
    /// tam typicky modely stáhne (Image Studio je vidí přes ComfyUI API). Pro sd-scripts
    /// trénink potřebujeme absolutní cestu — proto držíme mapování v <see cref="_baseModelPaths"/>.
    /// </summary>
    [RelayCommand]
    public async Task RefreshBaseModelsAsync()
    {
        var settings   = _settings.Settings;
        var aiModelsDir = AppPaths.ResolveModelsDirectory(settings.ModelsDirectory);

        // Lokace, ve kterých hledáme — (display prefix, absolutní path).
        // checkpoints/ = SD/SDXL; unet/ + diffusion_models/ = FLUX UNET modely
        // (FLUX LoRA trénink na nich, vč. flux1-dev / kontext fp8).
        var scanLocations = new List<(string Label, string Dir)>
        {
            ("AI Studio", Path.Combine(aiModelsDir, "checkpoints")),
            ("AI Studio", Path.Combine(aiModelsDir, "unet")),
            ("AI Studio", Path.Combine(aiModelsDir, "diffusion_models")),
        };

        // ComfyUI lokace — pokud máme nastavený directory, hledáme tam taky.
        // ComfyUI Portable má pevnou strukturu {ComfyUiDir}/models/{checkpoints,unet,…}.
        if (!string.IsNullOrWhiteSpace(settings.ComfyUiDirectory))
        {
            var comfyModels = Path.Combine(settings.ComfyUiDirectory, "models");
            scanLocations.Add(("ComfyUI", Path.Combine(comfyModels, "checkpoints")));
            scanLocations.Add(("ComfyUI", Path.Combine(comfyModels, "unet")));
            scanLocations.Add(("ComfyUI", Path.Combine(comfyModels, "diffusion_models")));
        }

        var found = await Task.Run(() =>
        {
            var results = new List<(string Display, string FullPath)>();
            foreach (var (label, dir) in scanLocations)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var ext in new[] { "*.safetensors", "*.ckpt" })
                    foreach (var path in Directory.EnumerateFiles(dir, ext, SearchOption.AllDirectories))
                    {
                        var relName = Path.GetRelativePath(dir, path).Replace('\\', '/');
                        // Pokud máme víc lokací, prefixujeme display názvem zdroje pro
                        // rozlišení duplicit (uživatel může mít stejný model na obou
                        // místech — ukážeme oba ať si vybere).
                        var display = scanLocations.Count > 1
                            ? $"[{label}] {relName}"
                            : relName;
                        results.Add((display, path));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "LoraTrainingPane: scan {Dir} selhal", dir);
                }
            }
            return results
                .OrderBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        Dispatcher.UIThread.Post(() =>
        {
            AvailableBaseModels.Clear();
            _baseModelPaths.Clear();

            foreach (var (display, fullPath) in found)
            {
                AvailableBaseModels.Add(display);
                _baseModelPaths[display] = fullPath;
            }

            // Auto-select první stažený, pokud žádný není vybraný
            if (string.IsNullOrEmpty(SelectedBaseModel) && AvailableBaseModels.Count > 0)
                SelectedBaseModel = AvailableBaseModels[0];

            // Sync „Staženo" badge u doporučených karet — soubor mohl nově přibýt
            // (DL dokončen, externí kopie, atd.)
            RefreshRecommendedDownloadedFlags();
        });
    }

    /// <summary>
    /// Otevře file picker a přidá ručně zvolený checkpoint do seznamu. Užitečné když
    /// uživatel má model na netradiční cestě (např. externí disk) a nechce ho
    /// přesouvat. Vybraný soubor se přidá s prefixem [Vlastní] a hned se zvolí.
    /// </summary>
    [RelayCommand]
    private async Task BrowseBaseModelAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Vyber základní model (.safetensors / .ckpt)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Checkpoint modely") { Patterns = new[] { "*.safetensors", "*.ckpt" } }
            }
        });

        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        if (!File.Exists(path)) return;

        var display = $"[Vlastní] {Path.GetFileName(path)}";

        Dispatcher.UIThread.Post(() =>
        {
            // Pokud už tam je (uživatel vybral podruhé stejný), jen vyber
            if (!AvailableBaseModels.Contains(display))
            {
                AvailableBaseModels.Add(display);
                _baseModelPaths[display] = path;
            }
            SelectedBaseModel = display;
        });
    }

    /// <summary>
    /// Vrátí absolutní cestu k vybranému checkpointu. Pokud nic nevybráno
    /// nebo se cesta v mapování neztratila, vrátí null.
    /// </summary>
    private string? ResolveSelectedBaseModelPath()
    {
        if (string.IsNullOrEmpty(SelectedBaseModel)) return null;
        return _baseModelPaths.TryGetValue(SelectedBaseModel, out var path) ? path : null;
    }

    /// <summary>
    /// Reaguje na update ze SystemMonitorService — typicky 1× za 2.5 s. Pro UI
    /// nás zajímá hlavně první sample (po něm máme GPU info), pak už změny
    /// VRAM jsou pro náš HW label irelevantní.
    /// </summary>
    private void OnSystemStatusUpdated(object? _, AIStudio.Core.Models.SystemStatus __)
        => Dispatcher.UIThread.Post(DetectHardware);

    /// <summary>
    /// Detekce HW — VRAM size + vendor pro odhad rychlosti a varování.
    /// Volá se při startu (kdy Current je typicky null) a znovu z
    /// <see cref="OnSystemStatusUpdated"/> jakmile přijde první sample.
    /// </summary>
    private void DetectHardware()
    {
        try
        {
            var cur = _monitor?.Current;
            if (cur is null || !cur.GpuAvailable)
            {
                HwInfoLabel        = "Bez GPU — trénink na CPU nedoporučujeme";
                HwSupportsTraining = false;
                HwWarningText      = "Bez GPU bude trénink trvat desítky hodin. Zvol prosím PC s NVIDIA GPU (8+ GB VRAM).";
                return;
            }

            var vramGb = cur.VramTotalGb;
            var name   = cur.GpuName ?? "GPU";

            // Zapamatuj poslední platnou hodnotu — při startu tréninku může být
            // _monitor.Current.VramTotalGb dočasně 0 (mezi vzorky) a bez cache by
            // adaptivní block-swap spadl do nejhoršího případu (max swap = pomalé).
            if (vramGb > 0) _lastKnownVramGb = vramGb;

            HwInfoLabel = $"{name} · {vramGb:F0} GB VRAM";

            if (vramGb < 6)
            {
                HwSupportsTraining = false;
                HwWarningText      = "Příliš málo VRAM (<6 GB) pro trénink SDXL LoRA. SD 1.5 LoRA může jít, ale je riziko OOM.";
            }
            else if (vramGb < 8)
            {
                HwSupportsTraining = true;
                HwWarningText      = "Hraniční VRAM (6-8 GB). Pro SDXL budu auto-aktivovat gradient checkpointing + batch=1. SD 1.5 v pohodě.";
            }
            else if (vramGb < 12)
            {
                HwSupportsTraining = true;
                HwWarningText      = string.Empty;  // OK, žádné varování
            }
            else
            {
                HwSupportsTraining = true;
                HwWarningText      = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoraTrainingPane: detekce HW selhala");
            HwInfoLabel = "GPU detekce selhala";
        }
    }

    // ── Doporučené parametry podle base modelu ────────────────────────────────

    partial void OnSelectedBaseModelChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        // Filename pro DefaultsFor — SelectedBaseModel může nést display prefix
        // („[ComfyUI] ", „[Vlastní] "), detekce by ho jinak interpretovala chybně.
        var filename = Path.GetFileName(ResolveSelectedBaseModelPath() ?? value);

        // Aplikuj doporučené default parametry, pokud uživatel ještě nemění nic
        // (tj. má je na initial defaults). Pokud už si rank zvedl, neměníme to.
        var defaults = LoraTrainingParameters.DefaultsFor(filename);

        // Heuristika "uživatel zatím needitoval" — porovnáme s našimi sticky defaults.
        // Pro MVP prostě vždycky aplikujeme — pokročilý uživatel může přepsat zpátky.
        Rank         = defaults.Rank;
        Alpha        = defaults.Alpha;
        LearningRate = defaults.LearningRate;
        BatchSize    = defaults.BatchSize;

        // Změna base modelu resetuje i auto-doporučení kroků (nové optimum dle typu).
        _userTouchedSteps = false;
        UpdateRecommendedSteps();
    }

    // ── Dataset operace ───────────────────────────────────────────────────────

    /// <summary>
    /// Otevře file picker a přidá vybrané obrázky do <see cref="DatasetItems"/>.
    /// Drag&drop alternativa je v code-behindu LoraLibraryPageView.
    /// </summary>
    [RelayCommand]
    private async Task AddImagesFromPickerAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Přidat obrázky do datasetu",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Obrázky") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });

        foreach (var f in files)
        {
            var p = f.Path.LocalPath;
            if (!File.Exists(p)) continue;
            AddDatasetImage(p);
        }
    }

    /// <summary>
    /// Přidá obrázek do datasetu — volaná z file pickeru nebo z drag&drop handleru.
    /// Deduplikuje podle absolutní cesty.
    /// </summary>
    public void AddDatasetImage(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return;
        if (DatasetItems.Any(i => string.Equals(i.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase)))
            return;

        DatasetItems.Add(new LoraDatasetItemViewModel(imagePath, RemoveDatasetItemAsync));
    }

    private Task RemoveDatasetItemAsync(LoraDatasetItemViewModel item)
    {
        DatasetItems.Remove(item);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearDataset()
    {
        DatasetItems.Clear();
    }

    // ── Auto-captioning ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartCaptioning))]
    private bool _isCaptioning;

    [ObservableProperty] private string _captionStatusLabel = string.Empty;
    [ObservableProperty] private int    _captionDone;
    [ObservableProperty] private int    _captionTotal;
    [ObservableProperty] private double _captionProgress;   // 0-100

    /// <summary>Styl auto-captionu: <c>blip</c> (foto) nebo <c>wd14</c> (anime).</summary>
    [ObservableProperty] private string _captionStyle = "blip";

    /// <summary>
    /// Režim „jen trigger token" — popisky fotek se při tréninku ignorují a nahradí
    /// se samotným trigger tokenem (název LoRA). Zapni pro LoRA na konkrétní OSOBU /
    /// POSTAVU / OBLIČEJ: identita se naváže na token místo na popisná slova.
    /// Default true — appka cílí hlavně na person/character LoRA. Pro styl/koncept vypni.
    /// </summary>
    [ObservableProperty] private bool _tokenOnlyCaptions = true;

    public IReadOnlyList<(string Value, string Label)> CaptionStyles { get; } = new[]
    {
        ("blip", "BLIP (fotorealistic)"),
        ("wd14", "WD14 tagger (anime)"),
    };

    /// <summary>True když je service dostupná, máme aspoň 1 obrázek, a nic neběží.</summary>
    public bool CanStartCaptioning =>
        _captionService is not null && !IsCaptioning && !IsTraining && DatasetItems.Count > 0;

    /// <summary>True když je auto-captioning vůbec dostupný (DI dodala service).</summary>
    public bool IsCaptioningSupported => _captionService is not null;

    [RelayCommand]
    private async Task GenerateCaptionsAsync()
    {
        if (_captionService is null || DatasetItems.Count == 0) return;

        IsCaptioning       = true;
        CaptionDone        = 0;
        CaptionTotal       = DatasetItems.Count;
        CaptionProgress    = 0;
        CaptionStatusLabel = "Spouštím auto-captioning…";

        // Označ items jako captioning pro UI spinner per-card
        foreach (var item in DatasetItems) item.IsCaptioning = true;

        _captionCts = new CancellationTokenSource();
        var progress = new Progress<CaptionProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            CaptionDone        = p.Done;
            CaptionTotal       = p.Total;
            CaptionProgress    = p.Total > 0 ? (double)p.Done / p.Total * 100 : 0;
            CaptionStatusLabel = p.Done >= p.Total
                ? "Hotovo"
                : $"Popisek {p.Done}/{p.Total}: {p.CurrentImageName}";
        }));

        try
        {
            var paths = DatasetItems.Select(i => i.ImagePath).ToList();
            var captions = await _captionService.CaptionAsync(
                paths, CaptionStyle, progress, _captionCts.Token);

            // Aplikuj výsledky zpátky do UI items (jen pokud uživatel ještě nenapsal vlastní)
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in DatasetItems)
                {
                    if (captions.TryGetValue(item.ImagePath, out var caption) &&
                        string.IsNullOrWhiteSpace(item.Caption))
                    {
                        item.Caption = caption;
                    }
                }
                CaptionStatusLabel = $"✓ Vygenerováno {captions.Count}/{paths.Count} popisků";
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => CaptionStatusLabel = "Auto-captioning zrušen");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LoraTrainingPane: captioning selhal");
            Dispatcher.UIThread.Post(() => CaptionStatusLabel = $"❌ {ex.Message}");
        }
        finally
        {
            IsCaptioning = false;
            foreach (var item in DatasetItems) item.IsCaptioning = false;
            _captionCts?.Dispose();
            _captionCts = null;
            OnPropertyChanged(nameof(CanStartCaptioning));
        }
    }

    [RelayCommand]
    private void CancelCaptioning()
    {
        try { _captionCts?.Cancel(); }
        catch (Exception ex) { Log.Warning(ex, "LoraTrainingPane: cancel captioning selhal"); }
    }

    // ── Trénink: start / cancel ───────────────────────────────────────────────

    // ── Code of Conduct (první spuštění tréninku) ─────────────────────────────

    /// <summary>True když je třeba ukázat CoC dialog místo startu tréninku.</summary>
    [ObservableProperty] private bool _isCodeOfConductVisible;

    [RelayCommand]
    private void AcceptCodeOfConduct()
    {
        _settings.Settings.LoraTrainingCodeOfConductAccepted = true;
        _ = _settings.SaveAsync();
        IsCodeOfConductVisible = false;
        // Po souhlasu rovnou pokračujeme s tréninkem
        _ = StartTrainingAsync();
    }

    [RelayCommand]
    private void DeclineCodeOfConduct() => IsCodeOfConductVisible = false;

    /// <summary>
    /// Najde FLUX text encodery (clip_l, t5xxl) + VAE (ae) v Models složce —
    /// v podsložkách clip/ a vae/ (kam je ukládá FluxDependencyService) i v rootu.
    /// </summary>
    private static (string? ClipL, string? T5, string? Ae) FindFluxDeps(string modelsRoot)
    {
        string? Find(string sub, params string[] files)
        {
            foreach (var f in files)
            {
                var inSub  = Path.Combine(modelsRoot, sub, f);
                var inRoot = Path.Combine(modelsRoot, f);
                if (File.Exists(inSub))  return inSub;
                if (File.Exists(inRoot)) return inRoot;
            }
            return null;
        }
        var clipL = Find("clip", "clip_l.safetensors");
        var t5    = Find("clip", "t5xxl_fp8_e4m3fn.safetensors", "t5xxl_fp16.safetensors");
        var ae    = Find("vae",  "ae.safetensors");
        return (clipL, t5, ae);
    }

    [RelayCommand]
    private async Task StartTrainingAsync()
    {
        if (!CanStartTraining) return;

        // První spuštění → CoC dialog. Po souhlasu se metoda zavolá znovu.
        if (!_settings.Settings.LoraTrainingCodeOfConductAccepted)
        {
            IsCodeOfConductVisible = true;
            return;
        }

        var modelsRoot = AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
        var baseModelPath = ResolveSelectedBaseModelPath()
            ?? throw new InvalidOperationException(
                $"Nelze rozeznat cestu k '{SelectedBaseModel}'. Klikni Obnovit a zkus znovu.");
        var outputDir     = Path.Combine(modelsRoot, "loras");

        var dataset = DatasetItems
            .Select(i => new LoraTrainingImage(i.ImagePath, i.Caption ?? string.Empty))
            .ToList();

        // Block swapping (FLUX) adaptivně dle VRAM. Na 24 GB stačí gradient
        // checkpointing → 0 bloků (rychlé, ~3-5 s/krok). Na slabších kartách víc.
        // BEZ toho na 24 GB padalo OOM jen kvůli vypnutému gradient checkpointingu;
        // swap 18 ho sice „spravil", ale zpomalil na ~60 s/krok (27 h pro 1500).
        // Bereme max(live, poslední známá) — live může být při startu dočasně 0.
        var vramGb = Math.Max(_monitor?.Current?.VramTotalGb ?? 0, _lastKnownVramGb);
        var blocksToSwap = vramGb >= 20 ? 0
                         : vramGb >= 16 ? 8
                         : vramGb >= 12 ? 16
                         : 24;
        Log.Information(
            "LoRA trénink: VRAM={Vram:F1} GB → blocks_to_swap={Blocks} (0 = bez swapu, rychlé)",
            vramGb, blocksToSwap);

        var parameters = new LoraTrainingParameters
        {
            Rank                  = Rank,
            Alpha                 = Alpha,
            Steps                 = Steps,
            LearningRate          = LearningRate,
            BatchSize             = BatchSize,
            Optimizer             = SelectedOptimizer,
            // Gradient checkpointing zapínáme na hraniční VRAM; pro FLUX ho trainer
            // vynutí vždy (FLUX se bez něj nevejde ani na 24 GB).
            GradientCheckpointing = vramGb < 12,
            BlocksToSwap          = blocksToSwap,
            MixedPrecisionFp16    = true,
            Resolution            = BaseModelTypeLabel == "SD 1.5" ? 512 : 1024,
        };

        // FLUX trénink potřebuje samostatné clip_l/t5/ae — zajisti je (auto-download)
        // a resolvuj cesty. Sdílíme je s FLUX generováním (stejná Models složka).
        string? fluxClipL = null, fluxT5 = null, fluxAe = null;
        if (baseModelPath.Contains("flux", StringComparison.OrdinalIgnoreCase))
        {
            if (_fluxDeps is not null && !_fluxDeps.AreDependenciesPresent(modelsRoot))
            {
                IsTraining = true;
                StatusLine = "Stahuji FLUX závislosti (CLIP-L, T5, VAE)…";
                using var depCts = new CancellationTokenSource();
                try { await _fluxDeps.EnsureAsync(modelsRoot, _settings.Settings.HuggingFaceToken, depCts.Token); }
                catch (Exception ex) { Log.Warning(ex, "LoRA: stažení FLUX závislostí selhalo"); }
            }
            (fluxClipL, fluxT5, fluxAe) = FindFluxDeps(modelsRoot);
        }

        var request = new LoraTrainingRequest(
            Name:            TrainingName.Trim(),
            BaseModelPath:   baseModelPath,
            Dataset:         dataset,
            Parameters:      parameters,
            OutputDirectory: outputDir,
            FluxClipLPath:   fluxClipL,
            FluxT5Path:      fluxT5,
            FluxAePath:      fluxAe,
            TokenOnlyCaptions: TokenOnlyCaptions);

        // Reset stavu pro UI
        IsTraining       = true;
        IsResultSuccess  = false;
        IsResultError    = false;
        ResultMessage    = string.Empty;
        CurrentStep      = 0;
        TotalSteps       = Steps;
        CurrentProgress  = 0;
        StatusLine       = "Spouštím trénink…";
        ElapsedLabel     = string.Empty;
        RemainingLabel   = string.Empty;
        LossLabel        = string.Empty;

        _cts = new CancellationTokenSource();
        var progress = new Progress<LoraTrainingProgress>(p => Dispatcher.UIThread.Post(() => ApplyProgress(p)));

        try
        {
            var result = await Task.Run(async () =>
                await _trainer.TrainAsync(request, progress, _cts.Token), _cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                if (result.Success)
                {
                    IsResultSuccess = true;
                    ResultMessage   = $"✓ LoRA hotová za {FormatDuration(result.TotalTime)} — {result.OutputFilePath}";
                    StatusLine      = "Hotovo";
                    CurrentProgress = 100;
                }
                else
                {
                    IsResultError = true;
                    ResultMessage = $"❌ Trénink selhal: {result.ErrorMessage}";
                    StatusLine    = "Selhalo";
                }
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsResultError = true;
                ResultMessage = "Trénink zrušen uživatelem.";
                StatusLine    = "Zrušeno";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LoraTrainingPane: trénink hodil výjimku");
            Dispatcher.UIThread.Post(() =>
            {
                IsResultError = true;
                ResultMessage = $"❌ {ex.Message}";
                StatusLine    = "Chyba";
            });
        }
        finally
        {
            IsTraining = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelTraining()
    {
        try { _cts?.Cancel(); }
        catch (Exception ex) { Log.Warning(ex, "LoraTrainingPane: cancel selhal"); }
    }

    private void ApplyProgress(LoraTrainingProgress p)
    {
        CurrentStep     = p.CurrentStep;
        TotalSteps      = p.TotalSteps;
        CurrentProgress = p.TotalSteps > 0 ? (double)p.CurrentStep / p.TotalSteps * 100 : 0;
        StatusLine      = p.StatusLine;
        ElapsedLabel    = FormatDuration(p.Elapsed);
        RemainingLabel  = p.EstimatedRemaining.HasValue
            ? $"zbývá ~{FormatDuration(p.EstimatedRemaining.Value)}"
            : string.Empty;
        LossLabel       = p.CurrentLoss.HasValue ? $"loss {p.CurrentLoss.Value:F4}" : string.Empty;
    }

    private static string FormatDuration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1
            ? $"{t.Minutes}m {t.Seconds}s"
            : $"{t.Seconds}s";
}
