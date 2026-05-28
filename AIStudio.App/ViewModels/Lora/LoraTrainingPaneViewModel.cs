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

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _captionCts;

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
    [NotifyPropertyChangedFor(nameof(CanStartTraining), nameof(IsIdle))]
    private bool _isTraining;

    [ObservableProperty] private int    _currentStep;
    [ObservableProperty] private int    _totalSteps;
    [ObservableProperty] private double _currentProgress;       // 0-100
    [ObservableProperty] private string _statusLine = string.Empty;
    [ObservableProperty] private string _elapsedLabel = string.Empty;
    [ObservableProperty] private string _remainingLabel = string.Empty;
    [ObservableProperty] private string _lossLabel = string.Empty;

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
        ISystemMonitorService?         monitor        = null,
        ILoraCaptionService?           captionService = null)
    {
        AvailableBaseModels.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasAvailableBaseModels));

        _trainer        = trainer;
        _deps           = deps;
        _settings       = settings;
        _monitor        = monitor;
        _captionService = captionService;

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
        };

        DetectHardware();
        _ = RefreshBaseModelsAsync();
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

        // Lokace, ve kterých hledáme — (display prefix, absolutní path k checkpoints/)
        var scanLocations = new List<(string Label, string Dir)>
        {
            ("AI Studio", Path.Combine(aiModelsDir, "checkpoints")),
        };

        // ComfyUI lokace — pokud máme nastavený directory, hledáme tam taky.
        // ComfyUI Portable má pevnou strukturu {ComfyUiDir}/models/checkpoints/.
        if (!string.IsNullOrWhiteSpace(settings.ComfyUiDirectory))
        {
            scanLocations.Add(("ComfyUI",
                Path.Combine(settings.ComfyUiDirectory, "models", "checkpoints")));
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
        Steps        = defaults.Steps;
        LearningRate = defaults.LearningRate;
        BatchSize    = defaults.BatchSize;
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

        var parameters = new LoraTrainingParameters
        {
            Rank                  = Rank,
            Alpha                 = Alpha,
            Steps                 = Steps,
            LearningRate          = LearningRate,
            BatchSize             = BatchSize,
            Optimizer             = SelectedOptimizer,
            // Pro hraniční VRAM auto-aktivujeme gradient checkpointing
            GradientCheckpointing = (_monitor?.Current?.VramTotalGb ?? 0) < 12,
            MixedPrecisionFp16    = true,
            Resolution            = BaseModelTypeLabel == "SD 1.5" ? 512 : 1024,
        };

        var request = new LoraTrainingRequest(
            Name:            TrainingName.Trim(),
            BaseModelPath:   baseModelPath,
            Dataset:         dataset,
            Parameters:      parameters,
            OutputDirectory: outputDir);

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
