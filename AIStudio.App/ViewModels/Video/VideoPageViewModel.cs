using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.App.ViewModels.Video;

/// <summary>
/// Samostatná záložka „Video" — generování videa přes Wan 2.1 (text→video i obrázek→video).
/// Výběr modelu, parametry (délka/FPS/kroky/CFG), stažení velkých Wan modelů s progresem
/// a samotné generování. Výsledek se uloží do galerie (MediaType=video).
///
/// <para>Vědomě oddělené od Image Studia — video má jiné modely, závislosti i workflow.</para>
/// </summary>
public partial class VideoPageViewModel : ViewModelBase
{
    private readonly IVideoGenerationService _videoGen;
    private readonly IWanDependencyService   _wanDeps;
    private readonly ISettingsService        _settings;
    private readonly INavigationService      _nav;
    private readonly ILoraLibraryService?    _loraLibrary;

    private CancellationTokenSource? _genCts;
    private readonly System.Diagnostics.Stopwatch _genStopwatch = new();

    public VideoPageViewModel(
        IVideoGenerationService videoGen,
        IWanDependencyService   wanDeps,
        ISettingsService        settings,
        INavigationService      nav,
        ILoraLibraryService?    loraLibrary = null)
    {
        _videoGen    = videoGen;
        _wanDeps     = wanDeps;
        _settings    = settings;
        _nav         = nav;
        _loraLibrary = loraLibrary;

        SelectedLoras.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLoras));

        ApplyModelDefaults();
        RefreshDepsState();
        _ = LoadLorasAsync();
    }

    // ── Model + parametry ──────────────────────────────────────────────────────

    public ObservableCollection<WanVideoModel> Models { get; } = new(WanModels.All);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageToVideo), nameof(CanGenerate),
                              nameof(DimensionsLabel), nameof(ModelDescription))]
    private WanVideoModel _selectedModel = WanModels.T2V_14B;

    /// <summary>Presety rozlišení (480p/720p, na šířku/výšku, čtverec).</summary>
    public ObservableCollection<VideoResolution> Resolutions { get; } = new(VideoResolution.All);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DimensionsLabel))]
    private VideoResolution _selectedResolution = VideoResolution.Landscape480;

    [ObservableProperty] private int _width  = 832;
    [ObservableProperty] private int _height = 480;

    partial void OnSelectedResolutionChanged(VideoResolution value)
    {
        Width  = value.Width;
        Height = value.Height;
        OnPropertyChanged(nameof(DimensionsLabel));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private string _prompt = string.Empty;

    /// <summary>Negativní prompt (co NEchceš). Prázdné = použije se oficiální Wan default.</summary>
    [ObservableProperty] private string _negativePrompt = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationLabel))]
    private int _length = 33;          // snímky

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationLabel))]
    private int _fps = 16;

    [ObservableProperty] private int    _steps = 30;
    [ObservableProperty] private double _cfg   = 6.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartImage), nameof(CanGenerate))]
    private string _startImagePath = string.Empty;

    /// <summary>Náhled vstupního obrázku (Image.Source neumí string cestu).</summary>
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _startImageBitmap;

    partial void OnStartImagePathChanged(string value)
    {
        StartImageBitmap?.Dispose();
        StartImageBitmap = null;
        if (string.IsNullOrEmpty(value) || !File.Exists(value)) return;
        try
        {
            using var s = File.OpenRead(value);
            StartImageBitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(s, 240);
        }
        catch (Exception ex) { Log.Warning(ex, "VideoPage: náhled vstupního obrázku selhal"); }
    }

    public bool IsImageToVideo  => SelectedModel?.Mode == WanVideoMode.ImageToVideo;
    public bool HasStartImage   => !string.IsNullOrEmpty(StartImagePath) && File.Exists(StartImagePath);
    public string ModelDescription => SelectedModel?.Description ?? string.Empty;
    public string DimensionsLabel  => $"{Width} × {Height}";
    public string DurationLabel     => Fps > 0 ? $"{Length} snímků ≈ {Length / (double)Fps:0.0} s" : $"{Length} snímků";

    partial void OnSelectedModelChanged(WanVideoModel value)
    {
        ApplyModelDefaults();
        RefreshDepsState();
    }

    private void ApplyModelDefaults()
    {
        var m = SelectedModel;
        if (m is null) return;
        // t2v 14B/1.3B → 30 kroků; i2v → 20 (oficiální Wan example).
        Steps = m.Mode == WanVideoMode.ImageToVideo ? 20 : 30;
        // Rozlišení dle modelu (480p/720p), zdroj pravdy pro generování je Width/Height.
        SelectedResolution = VideoResolution.Match(m.DefaultWidth, m.DefaultHeight);
        Width  = m.DefaultWidth;
        Height = m.DefaultHeight;
    }

    // ── Závislosti (velké modely) ──────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private bool _depsReady;

    [ObservableProperty] private string _missingDepsLabel = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private bool _isDownloadingDeps;
    [ObservableProperty] private string _depsStatus = string.Empty;

    private void RefreshDepsState()
    {
        var missing = _wanDeps.FindMissing(ModelsDir(), SelectedModel);
        DepsReady        = missing.Count == 0;
        MissingDepsLabel = DepsReady ? string.Empty : string.Join(", ", missing);
    }

    [RelayCommand]
    private async Task DownloadDependenciesAsync()
    {
        if (IsDownloadingDeps) return;
        var model = SelectedModel;
        ErrorMessage      = string.Empty;
        IsDownloadingDeps = true;

        // Poll service status do UI, dokud běží stahování.
        var poll = Task.Run(async () =>
        {
            while (IsDownloadingDeps)
            {
                var line = _wanDeps.DownloadStatusLine;
                Dispatcher.UIThread.Post(() => DepsStatus = line);
                try { await Task.Delay(500); } catch { break; }
            }
        });

        try
        {
            await _wanDeps.EnsureAsync(ModelsDir(), model);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoPage: stahování Wan závislostí selhalo");
            ErrorMessage = "Stahování modelů selhalo: " + ex.Message;
        }
        finally
        {
            IsDownloadingDeps = false;
            await poll;
            DepsStatus = string.Empty;
            RefreshDepsState();
        }
    }

    // ── LoRA (volitelné) ───────────────────────────────────────────────────────

    public ObservableCollection<string>   AvailableLoras { get; } = new();
    public ObservableCollection<LoraItem> SelectedLoras  { get; } = new();
    public bool HasLoras => SelectedLoras.Count > 0;

    [ObservableProperty] private string? _selectedLoraToAdd;
    [ObservableProperty] private double  _loraStrength = 1.0;

    [RelayCommand]
    private async Task LoadLorasAsync()
    {
        if (_loraLibrary is null) return;
        try
        {
            var all = await _loraLibrary.ListAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailableLoras.Clear();
                foreach (var l in all) AvailableLoras.Add(l);
            });
        }
        catch (Exception ex) { Log.Warning(ex, "VideoPage: načtení LoRA selhalo"); }
    }

    [RelayCommand]
    private void AddLora()
    {
        if (string.IsNullOrEmpty(SelectedLoraToAdd)) return;
        if (SelectedLoras.Any(l => l.Name == SelectedLoraToAdd)) return;
        SelectedLoras.Add(new LoraItem(SelectedLoraToAdd, LoraStrength, LoraStrength));
    }

    [RelayCommand]
    private void RemoveLora(LoraItem lora) => SelectedLoras.Remove(lora);

    // ── Generování ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private bool _isGenerating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressLabel))]
    private int _progress;

    [ObservableProperty] private string _statusLine = string.Empty;
    [ObservableProperty] private string _etaLabel = string.Empty;

    /// <summary>Procenta jako text pro UI („45 %").</summary>
    public string ProgressLabel => $"{Progress} %";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultVideoPath = string.Empty;

    public bool HasResult => !string.IsNullOrEmpty(ResultVideoPath) && File.Exists(ResultVideoPath);

    public bool CanGenerate =>
        DepsReady && !IsGenerating && !IsDownloadingDeps &&
        (IsImageToVideo ? HasStartImage : !string.IsNullOrWhiteSpace(Prompt));

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (!CanGenerate) return;

        using var cts = new CancellationTokenSource();
        _genCts      = cts;
        IsGenerating = true;
        Progress     = 0;
        EtaLabel     = string.Empty;
        ErrorMessage = string.Empty;
        StatusLine   = "Generuji video…";
        _genStopwatch.Restart();

        var model = SelectedModel;
        var seed  = Random.Shared.NextInt64(1, long.MaxValue);
        var req = new VideoGenerationRequest(
            Model:  model,
            Prompt: Prompt.Trim(),
            Width:  Width,
            Height: Height,
            Length: Length,
            Steps:  Steps,
            Cfg:    Cfg,
            Seed:   seed,
            StartImagePath: IsImageToVideo ? StartImagePath : null,
            Fps:    Fps,
            NegativePrompt: string.IsNullOrWhiteSpace(NegativePrompt) ? null : NegativePrompt.Trim(),
            Loras:  SelectedLoras.Count > 0 ? SelectedLoras.ToList() : null);

        var progress = new Progress<int>(p => Dispatcher.UIThread.Post(() => UpdateProgress(p)));
        try
        {
            var result = await _videoGen.GenerateAsync(req, progress, cts.Token);
            if (result.Success)
            {
                ResultVideoPath = result.FilePath ?? string.Empty;
                StatusLine      = "Hotovo — video je v galerii.";
            }
            else if (result.MissingDependencies is { Count: > 0 })
            {
                DepsReady        = false;
                MissingDepsLabel = string.Join(", ", result.MissingDependencies);
                ErrorMessage     = "Nejdřív stáhni potřebné Wan modely.";
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Generování selhalo.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Zrušeno.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VideoPage: generování videa selhalo");
            ErrorMessage = "Generování selhalo: " + ex.Message;
        }
        finally
        {
            IsGenerating = false;
            _genCts      = null;
            _genStopwatch.Stop();
            EtaLabel = string.Empty;
        }
    }

    /// <summary>Aktualizuje procenta a dopočítá ETA z uplynulého času.</summary>
    private void UpdateProgress(int p)
    {
        Progress = p;
        if (p is > 0 and < 100)
        {
            var elapsed   = _genStopwatch.Elapsed.TotalSeconds;
            var remaining = elapsed / p * (100 - p);
            EtaLabel = "zbývá ~" + FormatSeconds(remaining);
        }
        else
        {
            EtaLabel = string.Empty;
        }
    }

    private static string FormatSeconds(double s)
    {
        if (s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    [RelayCommand]
    private void CancelGenerate() => _genCts?.Cancel();

    // ── Vstupní obrázek (i2v) ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task PickStartImageAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Vybrat vstupní obrázek pro video",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Obrázek") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] },
            ],
        });

        if (files.Count > 0)
            StartImagePath = files[0].Path.LocalPath;
    }

    [RelayCommand]
    private void ClearStartImage() => StartImagePath = string.Empty;

    /// <summary>
    /// Nastaví vstupní obrázek a přepne na obrázek→video model — volá galerie přes
    /// tlačítko „Rozhýbat". Nenavigujeme tady (to dělá volající).
    /// </summary>
    public void StartFromImage(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return;
        if (SelectedModel?.Mode != WanVideoMode.ImageToVideo)
            SelectedModel = WanModels.I2V_480P_14B;
        StartImagePath = imagePath;
    }

    [RelayCommand]
    private void OpenGallery() => _nav.Navigate(NavigationPage.Gallery);

    [RelayCommand]
    private void OpenResultFile()
    {
        if (!HasResult) return;
        try { AIStudio.Infrastructure.Services.PlatformShell.Open(ResultVideoPath); }
        catch (Exception ex) { Log.Warning(ex, "VideoPage: otevření videa selhalo"); }
    }

    private string ModelsDir() =>
        AppPaths.ResolveModelsDirectory(_settings.Settings.ModelsDirectory);
}
