using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;

namespace AIStudio.App.ViewModels.ImageStudio;

public partial class ImageStudioPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IComfyService    _comfy;
    private readonly ISettingsService _settings;
    private readonly IImageRepository _imageRepo;

    [ObservableProperty] private ImageGeneratorViewModel? _activeGenerator;

    // ── ComfyUI status ────────────────────────────────────────────────────────
    [ObservableProperty] private string _comfyStatusMessage = "Inicializuji…";
    [ObservableProperty] private bool   _comfyRunning;
    [ObservableProperty] private bool   _comfyStarting;
    [ObservableProperty] private bool   _comfyError;

    /// <summary>True pokud ComfyUI není ani Running, Starting ani Error (idle/offline).</summary>
    public bool ComfyIdle => !ComfyRunning && !ComfyStarting && !ComfyError;

    /// <summary>
    /// True pokud uživatel nastavil cestu ke ComfyUI a v ní existuje main.py.
    /// Když false, místo „Spustit ComfyUI" tlačítka se v UI zobrazí
    /// „Otevřít Nastavení" — bez nakonfigurované cesty by Spustit stejně padlo.
    /// </summary>
    public bool ComfyConfigured
    {
        get
        {
            var dir = _settings.Settings.ComfyUiDirectory;
            return !string.IsNullOrWhiteSpace(dir)
                   && Directory.Exists(dir)
                   && File.Exists(Path.Combine(dir, "main.py"));
        }
    }

    /// <summary>Callback pro navigaci na jinou stránku — nastavuje MainWindowViewModel.</summary>
    public Action<NavigationPage>? RequestNavigate { get; set; }

    public ObservableCollection<ImageGeneratorViewModel> Generators { get; } = new();

    public ImageStudioPageViewModel(
        IComfyService    comfy,
        ISettingsService settings,
        IImageRepository imageRepo)
    {
        _comfy     = comfy;
        _settings  = settings;
        _imageRepo = imageRepo;

        _comfy.StatusChanged += OnComfyStatusChanged;
        SyncComfyStatus(_comfy.Status, _comfy.StatusMessage);

        var first = CreateGenerator();
        Generators.Add(first);
        SetActiveGenerator(first);

        // Při startu hned naskenuj lokální modely (i bez běžícího ComfyUI)
        // — uživatel uvidí v dropdownu, co má staženo
        _ = first.LoadCheckpointsAsync();

        // Když ModelManager stáhne / přidá / smaže model, refreshni dropdowny
        // ve všech otevřených generátorech
        _settings.ModelLibraryChanged += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var gen in Generators)
                    _ = gen.LoadCheckpointsAsync();
            });
        };

        // Když se v Nastavení změní cesta ke ComfyUI (a settings se uloží),
        // přepočítej ComfyConfigured — UI přepne mezi „Spustit" a „Otevřít Nastavení"
        _settings.SettingsSaved += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(ComfyConfigured));
                SyncComfyStatus(_comfy.Status, _comfy.StatusMessage);
            });

        // Load saved images into the first generator
        _ = first.LoadSavedImagesAsync();
    }

    [RelayCommand]
    private void OpenSettings() => RequestNavigate?.Invoke(NavigationPage.Settings);

    // ── ComfyUI lifecycle ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartComfyAsync()
    {
        await _comfy.StartAsync();
    }

    [RelayCommand]
    private async Task StopComfyAsync()
    {
        await _comfy.StopAsync();
    }

    private void OnComfyStatusChanged(ComfyStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SyncComfyStatus(status, _comfy.StatusMessage);

            // When ComfyUI just came online, refresh checkpoints in every generator
            if (status == ComfyStatus.Running)
            {
                foreach (var gen in Generators)
                    _ = gen.LoadCheckpointsAsync();
            }
        });
    }

    private void SyncComfyStatus(ComfyStatus status, string message)
    {
        // Pokud uživatel ještě nenastavil cestu ke ComfyUI, místo holé "Zastaveno"
        // jasně řekneme co je třeba udělat — XAML k tomu zobrazí tlačítko do Nastavení.
        if (!ComfyConfigured)
        {
            ComfyStatusMessage = "ComfyUI není nakonfigurován — nastav cestu v Nastavení";
            ComfyRunning       = false;
            ComfyStarting      = false;
            ComfyError         = false;
            OnPropertyChanged(nameof(ComfyIdle));
            return;
        }

        ComfyStatusMessage = message;
        ComfyRunning       = status == ComfyStatus.Running;
        ComfyStarting      = status == ComfyStatus.Starting;
        ComfyError         = status == ComfyStatus.Error;
        OnPropertyChanged(nameof(ComfyIdle));
    }

    // ── Generator tabs ────────────────────────────────────────────────────────

    [RelayCommand]
    private void AddGenerator()
    {
        var gen = CreateGenerator();
        Generators.Add(gen);
        SetActiveGenerator(gen);
    }

    [RelayCommand]
    private void SelectGenerator(ImageGeneratorViewModel gen) => SetActiveGenerator(gen);

    [RelayCommand]
    private void CloseGenerator(ImageGeneratorViewModel gen)
    {
        if (Generators.Count <= 1) return;
        var idx = Generators.IndexOf(gen);
        Generators.Remove(gen);
        if (ActiveGenerator == gen)
            SetActiveGenerator(Generators.ElementAtOrDefault(Math.Max(0, idx - 1)));
    }

    private void SetActiveGenerator(ImageGeneratorViewModel? gen)
    {
        // Clear IsActive on all, then set on the chosen one
        foreach (var g in Generators)
            g.IsActive = false;
        if (gen is not null)
            gen.IsActive = true;
        ActiveGenerator = gen;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ImageGeneratorViewModel CreateGenerator()
    {
        var gen = new ImageGeneratorViewModel(_comfy, _settings, _imageRepo);
        // LoadCheckpointsAsync sloučí ComfyUI i lokální sken — voláme ho vždy,
        // aby uživatel hned viděl stažené modely v dropdownu (ať už ComfyUI běží či ne).
        _ = gen.LoadCheckpointsAsync();
        return gen;
    }

    public async ValueTask DisposeAsync()
    {
        _comfy.StatusChanged -= OnComfyStatusChanged;
        await ValueTask.CompletedTask;
    }
}
