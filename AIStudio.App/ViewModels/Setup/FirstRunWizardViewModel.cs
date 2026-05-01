using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Setup;

public partial class FirstRunWizardViewModel : ViewModelBase
{
    private readonly ISettingsService      _settings;
    private readonly ISystemMonitorService _monitor;

    // ── Průchod kroky ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsStep0), nameof(IsStep1), nameof(IsStep2),
        nameof(IsStep3), nameof(IsStep4),
        nameof(IsLastStep), nameof(CanGoBack), nameof(NextButtonText))]
    private int _currentStep;

    public bool IsStep0 => CurrentStep == 0;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    public bool IsLastStep  => CurrentStep == 4;
    public bool CanGoBack   => CurrentStep > 0;
    public string NextButtonText => IsLastStep ? "Spustit AI Studio →" : "Pokračovat →";

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

    // ── Krok 3 — API tokeny ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHfToken))]
    private string _huggingFaceToken = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCivitaiKey))]
    private string _civitaiApiKey = string.Empty;

    public bool HasHfToken    => !string.IsNullOrEmpty(HuggingFaceToken);
    public bool HasCivitaiKey => !string.IsNullOrEmpty(CivitaiApiKey);

    // ── Událost dokončení ─────────────────────────────────────────────────────

    public event EventHandler? WizardCompleted;

    // ── Konstruktor ───────────────────────────────────────────────────────────

    public FirstRunWizardViewModel(ISettingsService settings, ISystemMonitorService monitor)
    {
        _settings = settings;
        _monitor  = monitor;
    }

    public void Initialize()
    {
        var s = _settings.Settings;

        ModelsDirectory  = string.IsNullOrEmpty(s.ModelsDirectory)
            ? DefaultModelsDir : s.ModelsDirectory;
        UseGpu           = s.UseGpu;
        HuggingFaceToken = s.HuggingFaceToken;
        CivitaiApiKey    = s.CivitaiApiKey;

        // Zaregistruj se na první status update pro detekci GPU
        _monitor.StatusUpdated += OnFirstStatusUpdate;
        _ = _monitor.StartAsync();

        // Timeout — pokud do 6 vteřin nic, označíme GPU jako nenalezené
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
        CurrentStep++;
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack) CurrentStep--;
    }

    private void Finish()
    {
        var s = _settings.Settings;

        s.ModelsDirectory  = ModelsDirectory == DefaultModelsDir
            ? string.Empty : ModelsDirectory;
        s.UseGpu           = UseGpu;
        s.HuggingFaceToken = HuggingFaceToken.Trim();
        s.CivitaiApiKey    = CivitaiApiKey.Trim();
        s.SetupCompleted   = true;

        _ = _settings.SaveAsync();

        WizardCompleted?.Invoke(this, EventArgs.Empty);
    }

    // ── Nastavení složky (volané z code-behind) ───────────────────────────────

    public void SetModelsDirectory(string path) => ModelsDirectory = path;
}
