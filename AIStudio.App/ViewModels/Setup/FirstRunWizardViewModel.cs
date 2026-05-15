using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App.ViewModels.Setup;

public partial class FirstRunWizardViewModel : ViewModelBase
{
    private readonly ISettingsService      _settings;
    private readonly ISystemMonitorService _monitor;
    private readonly IComfyInstaller       _comfyInstaller;
    private readonly IGpuDetector?         _gpuDetector;

    // ── Průchod kroky ─────────────────────────────────────────────────────────
    //
    // 0 = Uvítání
    // 1 = Složka modelů
    // 2 = GPU detekce
    // 3 = API tokeny
    // 4 = ComfyUI install
    // 5 = Doporučené modely (NEW)
    // 6 = Souhrn

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsStep0), nameof(IsStep1), nameof(IsStep2),
        nameof(IsStep3), nameof(IsStep4), nameof(IsStep5), nameof(IsStep6),
        nameof(IsLastStep), nameof(CanGoBack), nameof(NextButtonText),
        nameof(CanGoNext))]
    private int _currentStep;

    public bool IsStep0 => CurrentStep == 0;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsStep5 => CurrentStep == 5;
    public bool IsStep6 => CurrentStep == 6;
    public bool IsLastStep  => CurrentStep == 6;
    public bool CanGoBack   => CurrentStep > 0 && !IsInstallingComfy;
    public string NextButtonText => IsLastStep ? "Spustit AI Studio →" : "Pokračovat →";

    /// <summary>
    /// True pokud uživatel může jít na další krok. False během instalace ComfyUI,
    /// aby nemohl wizard přerušit uprostřed stahování 3 GB archivu.
    /// </summary>
    public bool CanGoNext => !IsInstallingComfy;

    // ── Krok 1 — Složka modelů ────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryModelsDir))]
    private string _modelsDirectory = string.Empty;

    public static string DefaultModelsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio", "Models");

    public string SummaryModelsDir => string.IsNullOrWhiteSpace(ModelsDirectory)
        ? DefaultModelsDir : ModelsDirectory;

    // ── Krok 2 — GPU ──────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isDetectingGpu = true;
    [ObservableProperty] private bool   _gpuDetected;
    [ObservableProperty] private string _gpuName    = string.Empty;
    [ObservableProperty] private double _vramTotalGb;
    [ObservableProperty] private bool   _useGpu = true;

    public string GpuSummaryLine => GpuDetected
        ? $"{GpuName}  ({VramTotalGb:F1} GB VRAM)"
        : "GPU nebylo detekováno — bude použit CPU";

    /// <summary>
    /// Detekovaný GPU vendor + doporučený backend. Nastavuje <see cref="IGpuDetector"/>
    /// asynchronně z <see cref="Initialize"/>. Null dokud detekce neproběhne.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuDetailLine), nameof(HasGpuDetail))]
    private Gpu? _gpuDetail;

    public bool HasGpuDetail => GpuDetail is not null;

    /// <summary>
    /// Vendor-aware vysvětlení pro krok 2 wizardu. Říká uživateli rovnou pravdu:
    /// NVIDIA = plná CUDA podpora, AMD = Vulkan přijde, Intel = Vulkan přijde,
    /// neznámý vendor = poběží na CPU. UX cíl je nelhat uživateli o stavu.
    /// </summary>
    public string GpuDetailLine => GpuDetail?.Vendor switch
    {
        GpuVendor.Nvidia => "Plná CUDA podpora — LLM a obrázky poběží na GPU.",
        GpuVendor.Amd    => "AMD karta — LLM přes Vulkan (~70–80 % CUDA rychlosti), generování obrázků přes DirectML (pomalejší, ale funguje). Při prvním spuštění ComfyUI se jednorázově stáhne torch-directml (~700 MB).",
        GpuVendor.Intel  => "Intel GPU — LLM přes Vulkan, generování obrázků přes DirectML. Při prvním spuštění ComfyUI se jednorázově stáhne torch-directml (~700 MB).",
        GpuVendor.Apple  => "Apple Silicon — Metal backend přijde s macOS verzí AI Studia.",
        GpuVendor.Unknown => "Žádná GPU nebyla detekována — vše poběží na CPU. Můžete zkusit menší modely (Llama 3.2 3B).",
        _                 => string.Empty,
    };

    // ── Krok 3 — API tokeny ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHfToken))]
    private string _huggingFaceToken = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCivitaiKey))]
    private string _civitaiApiKey = string.Empty;

    public bool HasHfToken    => !string.IsNullOrEmpty(HuggingFaceToken);
    public bool HasCivitaiKey => !string.IsNullOrEmpty(CivitaiApiKey);

    // ── Krok 4 — ComfyUI install ──────────────────────────────────────────────

    /// <summary>True když je ComfyUI nainstalovaný (cesta existuje + main.py).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowComfyInstallButton),
                              nameof(ShowComfyInstalledBadge),
                              nameof(ComfySummaryLine))]
    private bool _isComfyInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowComfyInstallButton),
                              nameof(ShowComfyInstallProgress),
                              nameof(CanGoBack), nameof(CanGoNext))]
    private bool _isInstallingComfy;

    [ObservableProperty] private int    _comfyInstallPercent;
    [ObservableProperty] private string _comfyInstallStatus = string.Empty;
    [ObservableProperty] private string _comfyInstallSpeed  = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComfyInstallError), nameof(ShowComfyInstallButton))]
    private string _comfyInstallError = string.Empty;

    public bool HasComfyInstallError      => !string.IsNullOrEmpty(ComfyInstallError);
    public bool ShowComfyInstallButton    => !IsComfyInstalled && !IsInstallingComfy;
    public bool ShowComfyInstalledBadge   => IsComfyInstalled && !IsInstallingComfy;
    public bool ShowComfyInstallProgress  => IsInstallingComfy;

    /// <summary>Cesta kam se ComfyUI nainstaluje — používá <see cref="IComfyInstaller.DefaultInstallDirectory"/>.</summary>
    public string ComfyInstallDir => _comfyInstaller.DefaultInstallDirectory;

    /// <summary>Krátké shrnutí stavu ComfyUI pro souhrnný krok 5.</summary>
    public string ComfySummaryLine => IsComfyInstalled
        ? "ComfyUI je nainstalovaný — generování obrázků k dispozici"
        : "ComfyUI nenainstalován — generování obrázků zatím nedostupné";

    /// <summary>
    /// CancellationTokenSource pro probíhající instalaci. Wizard ji může zrušit
    /// pokud uživatel klikne na Zrušit nebo zavře okno.
    /// </summary>
    private CancellationTokenSource? _installCts;

    // ── Krok 5 — Doporučené modely ───────────────────────────────────────────
    //
    // VM vystavuje plochou wrap třídu RecommendedModelChoice s IsSelected,
    // aby AXAML mohl rovnou bindovat na CheckBox. Zdrojem pravdy jsou položky
    // ve <see cref="RecommendedModels.All"/>.

    /// <summary>
    /// Wrapper kolem <see cref="RecommendedModel"/> pro UI binding —
    /// hlavně přidá <see cref="IsSelected"/> a předpočítaný popisek velikosti.
    /// </summary>
    public partial class RecommendedModelChoice : ObservableObject
    {
        public RecommendedModel Model { get; }
        [ObservableProperty] private bool _isSelected;

        public string SizeLabel  => Model.SizeBytes switch
        {
            >= 1_073_741_824 => $"{Model.SizeBytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576     => $"{Model.SizeBytes / 1_048_576.0:F0} MB",
            _                => $"{Model.SizeBytes / 1024.0:F0} KB"
        };
        public string KindLabel  => Model.Kind == RecommendedModelKind.Chat
            ? "Chat (LLM)" : "Generování obrázků";

        public RecommendedModelChoice(RecommendedModel model, bool defaultSelected)
        {
            Model = model;
            _isSelected = defaultSelected;
        }
    }

    /// <summary>
    /// Seznam doporučených modelů pro UI binding (krok 5). Inicializován
    /// konzervativně na LowTier (3B + SD 1.5) — vejde se i do 4 GB VRAM.
    /// Po detekci GPU (<see cref="OnGpuDetailChanged"/>) se přepne na
    /// tier vhodný pro hardware (DefaultTier pro NVIDIA 8+ GB / Apple Silicon).
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<RecommendedModelChoice> RecommendedModelChoices { get; }
        = new(RecommendedModels.LowTier
            .Select(m => new RecommendedModelChoice(m, defaultSelected: true)));

    /// <summary>
    /// Když dorazí výsledek GPU detekce, přepočítáme doporučené modely.
    /// Zachováme stav (IsSelected) — pokud uživatel mezi tím něco odznačil,
    /// jeho volba má přednost. Pro nově přidané položky default = true.
    /// </summary>
    partial void OnGpuDetailChanged(Gpu? value)
    {
        var picked = RecommendedModels.PickForGpu(value);

        // Zachováme předchozí volby uživatele — pokud už něco vědomě odznačil,
        // nepřeházíme mu to zpátky na "zaškrtnuté" jen kvůli detekci GPU.
        var previousSelections = RecommendedModelChoices
            .ToDictionary(c => c.Model.Id, c => c.IsSelected);

        RecommendedModelChoices.Clear();
        foreach (var m in picked)
        {
            var wasSelected = previousSelections.TryGetValue(m.Id, out var sel) ? sel : true;
            RecommendedModelChoices.Add(new RecommendedModelChoice(m, defaultSelected: wasSelected));
        }

        OnPropertyChanged(nameof(HasSelectedRecommendedModels));
        OnPropertyChanged(nameof(SelectedRecommendedCount));
        OnPropertyChanged(nameof(RecommendedSummaryLine));
    }

    /// <summary>True pokud uživatel vybral aspoň jeden model k stažení.</summary>
    public bool HasSelectedRecommendedModels =>
        RecommendedModelChoices.Any(c => c.IsSelected);

    public int SelectedRecommendedCount =>
        RecommendedModelChoices.Count(c => c.IsSelected);

    public string RecommendedSummaryLine => SelectedRecommendedCount switch
    {
        0 => "Žádné modely nebudou staženy",
        1 => "1 model se stáhne na pozadí po startu aplikace",
        _ => $"{SelectedRecommendedCount} modely se stáhnou na pozadí po startu aplikace"
    };

    // ── Událost dokončení ─────────────────────────────────────────────────────

    public event EventHandler? WizardCompleted;

    // ── Konstruktor ───────────────────────────────────────────────────────────

    public FirstRunWizardViewModel(ISettingsService settings,
                                    ISystemMonitorService monitor,
                                    IComfyInstaller comfyInstaller,
                                    IGpuDetector? gpuDetector = null)
    {
        _settings        = settings;
        _monitor         = monitor;
        _comfyInstaller  = comfyInstaller;
        _gpuDetector     = gpuDetector;
    }

    public void Initialize()
    {
        var s = _settings.Settings;

        ModelsDirectory  = string.IsNullOrEmpty(s.ModelsDirectory)
            ? DefaultModelsDir : s.ModelsDirectory;
        UseGpu           = s.UseGpu;
        HuggingFaceToken = s.HuggingFaceToken;
        CivitaiApiKey    = s.CivitaiApiKey;

        // Detekce existující instalace ComfyUI — buď v nastavené cestě,
        // nebo ve výchozí (%LocalAppData%\AIStudio\ComfyUI). Pokud najdeme,
        // krok 4 hned ukáže badge „Nainstalovaný" a Install tlačítko se skryje.
        IsComfyInstalled = DetectComfyInstalled();

        // Zaregistruj se na první status update pro detekci GPU
        _monitor.StatusUpdated += OnFirstStatusUpdate;
        _ = _monitor.StartAsync();

        // GPU vendor detekce — paralelně s monitorem. Vrátí Gpu record
        // s detekovaným backendem (CUDA pro NVIDIA, Vulkan placeholder pro AMD/Intel,
        // Cpu pro Unknown). Wizard krok 2 ho použije pro vendor-specific text.
        if (_gpuDetector is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var detected = await _gpuDetector.DetectAsync();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => GpuDetail = detected);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Wizard: GPU vendor detekce selhala");
                }
            });
        }

        // Timeout — pokud do 6 vteřin nic, označíme GPU jako nenalezené.
        // POZN.: bez TaskScheduler.FromCurrentSynchronizationContext bychom byli
        // na threadpoolu — opravujeme přes Dispatcher.UIThread.Post.
        _ = Task.Delay(6000).ContinueWith(_ =>
        {
            if (!IsDetectingGpu) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _monitor.StatusUpdated -= OnFirstStatusUpdate;
                IsDetectingGpu = false;
                GpuDetected    = false;
                GpuName        = "Nepodařilo se detekovat";
                OnPropertyChanged(nameof(GpuSummaryLine));
            });
        });
    }

    /// <summary>
    /// Najde existující ComfyUI instalaci — buď v cestě nastavené v
    /// <see cref="AppSettings.ComfyUiDirectory"/>, nebo v default lokaci.
    /// </summary>
    private bool DetectComfyInstalled()
    {
        var existingDir = _settings.Settings.ComfyUiDirectory;

        // Pokud máme nastavenou cestu, ověříme přímo (umístění je hotovo)
        if (!string.IsNullOrWhiteSpace(existingDir) &&
            Directory.Exists(existingDir) &&
            File.Exists(Path.Combine(existingDir, "main.py")))
        {
            return true;
        }

        // Jinak ověříme default install cestu — někdo už mohl klepnout instalaci
        // v Nastavení a zapomněli jsme to v settings.json (např. wizard běží
        // znovu po reinstalu)
        return _comfyInstaller.DetectExisting(_comfyInstaller.DefaultInstallDirectory) is not null;
    }

    private void OnFirstStatusUpdate(object? sender, SystemStatus st)
    {
        _monitor.StatusUpdated -= OnFirstStatusUpdate;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            GpuDetected    = st.GpuAvailable;
            GpuName        = st.GpuName;
            VramTotalGb    = st.VramTotalGb;
            IsDetectingGpu = false;
            OnPropertyChanged(nameof(GpuSummaryLine));
        });
    }

    // ── Příkazy ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Next()
    {
        if (IsLastStep)
        {
            Finish();
            return;
        }
        if (!CanGoNext) return; // ochrana proti kliku během instalace
        CurrentStep++;
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack) CurrentStep--;
    }

    /// <summary>
    /// Krok 4: spustí auto-instalaci ComfyUI Portable do <see cref="ComfyInstallDir"/>.
    /// Progress se reportuje přes <see cref="ComfyInstallPercent"/> a <see cref="ComfyInstallStatus"/>.
    /// Pokud uživatel klepne Zrušit, <see cref="_installCts"/> instalaci přeruší.
    /// </summary>
    [RelayCommand]
    private async Task StartComfyInstallAsync()
    {
        if (IsInstallingComfy || IsComfyInstalled) return;

        _installCts        = new CancellationTokenSource();
        IsInstallingComfy  = true;
        ComfyInstallError  = string.Empty;
        ComfyInstallPercent = 0;
        ComfyInstallStatus  = "Příprava…";
        ComfyInstallSpeed   = string.Empty;

        Log.Information("Wizard: zahajuji instalaci ComfyUI do {Dir}", ComfyInstallDir);

        var progress = new Progress<ComfyInstallProgress>(p =>
        {
            ComfyInstallPercent = p.Percent;
            ComfyInstallStatus  = p.Message;
            ComfyInstallSpeed   = p.BytesPerSecond > 0
                ? $"{p.BytesPerSecond / 1_048_576.0:F1} MB/s"
                : string.Empty;
        });

        try
        {
            var (comfyDir, pythonPath) = await _comfyInstaller.InstallAsync(
                ComfyInstallDir, progress, _installCts.Token);

            // Uložíme cesty hned, aby je viděl ComfyService při Initialize po wizardu
            _settings.Settings.ComfyUiDirectory = comfyDir;
            _settings.Settings.PythonPath       = pythonPath;
            await _settings.SaveAsync();

            IsComfyInstalled  = true;
            ComfyInstallStatus = "Hotovo — ComfyUI je nainstalovaný";
            Log.Information("Wizard: ComfyUI úspěšně nainstalován do {Dir}", comfyDir);
        }
        catch (OperationCanceledException)
        {
            ComfyInstallError = "Instalace byla zrušena";
            Log.Information("Wizard: instalace ComfyUI zrušena uživatelem");
        }
        catch (Exception ex)
        {
            ComfyInstallError = ex.Message;
            Log.Error(ex, "Wizard: instalace ComfyUI selhala");
        }
        finally
        {
            IsInstallingComfy = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    [RelayCommand]
    private void CancelComfyInstall()
    {
        if (_installCts is null) return;
        Log.Information("Wizard: uživatel ruší instalaci ComfyUI");
        try { _installCts.Cancel(); } catch { /* race s Dispose */ }
    }

    /// <summary>
    /// Krok 4 lze také jednoduše přeskočit — uživatele nezajímá generování obrázků.
    /// Stejné jako klik Pokračovat, jen explicitní pro UI.
    /// </summary>
    [RelayCommand]
    private void SkipComfyInstall()
    {
        if (IsInstallingComfy) return;
        Log.Information("Wizard: uživatel přeskočil instalaci ComfyUI");
        CurrentStep++;
    }

    private async void Finish()
    {
        try
        {
            var s = _settings.Settings;

            s.ModelsDirectory  = ModelsDirectory == DefaultModelsDir
                ? string.Empty : ModelsDirectory;
            s.UseGpu           = UseGpu;
            s.HuggingFaceToken = HuggingFaceToken.Trim();
            s.CivitaiApiKey    = CivitaiApiKey.Trim();
            s.SetupCompleted   = true;

            // Pokud byl ComfyUI nainstalovaný, automaticky ho spouštět při startu
            // — uživatel jinak musí jít do Nastavení a zapnout AutoStart ručně.
            if (IsComfyInstalled)
                s.AutoStartComfyUi = true;

            // Uložíme IDs vybraných doporučených modelů. App po wizardu vyzvedne
            // PendingModelDownloads a spustí stahování přes IDownloadService.
            s.PendingModelDownloads = RecommendedModelChoices
                .Where(c => c.IsSelected)
                .Select(c => c.Model.Id)
                .ToList();

            // Awaitujeme save — fire-and-forget v původní verzi tiše ztrácel chyby
            await _settings.SaveAsync();
            Log.Information("Wizard: Finish — {N} modelů ke stažení, ComfyUI={Comfy}",
                            s.PendingModelDownloads.Count, IsComfyInstalled);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Wizard: uložení nastavení selhalo, ale wizard pokračuje");
        }

        WizardCompleted?.Invoke(this, EventArgs.Empty);
    }

    // ── Nastavení složky (volané z code-behind) ───────────────────────────────

    public void SetModelsDirectory(string path) => ModelsDirectory = path;
}
