using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Záložka „Z videa" v LoRA stránce — front-end pipeline video→LoRA. Uživatel vloží videa,
/// spustí <b>analýzu</b> (<see cref="IVideoLoraPipelineService"/> navzorkuje + popíše snímky),
/// v mřížce <b>vybere</b> snímky a předá je do existující trénovací záložky
/// (<see cref="LoraTrainingPaneViewModel"/>) — odtud už běží standardní trénink.
///
/// <para>Samotnou extrakci/captioning/trénink <b>neimplementuje</b> — jen orchestruje
/// existující služby.</para>
/// </summary>
public partial class VideoLoraPaneViewModel : ViewModelBase
{
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" };

    private readonly IVideoLoraPipelineService   _pipeline;
    private readonly LoraTrainingPaneViewModel    _trainingPane;
    private readonly Action                        _switchToTrainingTab;
    private CancellationTokenSource? _cts;

    /// <summary>Vstupní videa (absolutní cesty).</summary>
    public ObservableCollection<string> VideoPaths { get; } = new();

    /// <summary>Kandidátní snímky z poslední analýzy.</summary>
    public ObservableCollection<CandidateFrameViewModel> Candidates { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoVideos), nameof(CanAnalyze))]
    private int _videoCount;

    /// <summary>Kolik kandidátů navzorkovat z každého videa.</summary>
    [ObservableProperty] private int _framesPerVideo = 20;

    /// <summary>Styl popisku — blip (foto) / wd14 (anime).</summary>
    [ObservableProperty] private string _captionStyle = "blip";

    public IReadOnlyList<(string Value, string Label)> CaptionStyles { get; } = new[]
    {
        ("blip", "BLIP (fotorealistic)"),
        ("wd14", "WD14 tagger (anime)"),
    };

    /// <summary>Název/identita LoRA — předvyplní se do trénovací záložky při handoffu.</summary>
    [ObservableProperty] private string _loraName = string.Empty;

    /// <summary>Token-only popisky (osoba/postava) — předá se do tréninku.</summary>
    [ObservableProperty] private bool _tokenOnlyCaptions = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanAnalyze))]
    private bool _isAnalyzing;

    [ObservableProperty] private string _analyzeStatus = string.Empty;
    [ObservableProperty] private double _analyzeProgress;   // 0-100
    [ObservableProperty] private bool   _hasAnalyzeError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCountLabel), nameof(CanUseForTraining))]
    private int _selectedCount;

    public bool HasNoVideos => VideoCount == 0;
    public bool IsIdle => !IsAnalyzing;
    public bool HasCandidates => Candidates.Count > 0;
    public bool CanAnalyze => !IsAnalyzing && VideoCount > 0;

    /// <summary>Trénink potřebuje aspoň 5 snímků (stejně jako manuální dataset).</summary>
    public bool CanUseForTraining => !IsAnalyzing && SelectedCount >= 5;

    public string SelectedCountLabel => Candidates.Count == 0
        ? string.Empty
        : $"Vybráno {SelectedCount} z {Candidates.Count}";

    public VideoLoraPaneViewModel(
        IVideoLoraPipelineService pipeline,
        LoraTrainingPaneViewModel trainingPane,
        Action switchToTrainingTab)
    {
        _pipeline            = pipeline;
        _trainingPane        = trainingPane;
        _switchToTrainingTab = switchToTrainingTab;

        VideoPaths.CollectionChanged += (_, _) =>
        {
            VideoCount = VideoPaths.Count;
            OnPropertyChanged(nameof(HasNoVideos));
            OnPropertyChanged(nameof(CanAnalyze));
        };
        Candidates.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCandidates));
            OnPropertyChanged(nameof(SelectedCountLabel));
        };
    }

    // ── Vstupní videa ─────────────────────────────────────────────────────────

    /// <summary>Přidá video (dedup podle cesty). Volá se z pickeru i z drag&drop.</summary>
    public void AddVideo(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!VideoExtensions.Contains(Path.GetExtension(path))) return;
        if (VideoPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        VideoPaths.Add(path);
    }

    [RelayCommand]
    private async Task AddVideosFromPickerAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Přidat videa pro analýzu",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Videa")
                    { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v" } }
            }
        });

        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) AddVideo(p);
        }
    }

    [RelayCommand]
    private void RemoveVideo(string? path)
    {
        if (!string.IsNullOrEmpty(path)) VideoPaths.Remove(path);
    }

    [RelayCommand]
    private void ClearVideos() => VideoPaths.Clear();

    // ── Analýza ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (!CanAnalyze) return;

        Candidates.Clear();
        SelectedCount   = 0;
        HasAnalyzeError = false;
        IsAnalyzing     = true;
        AnalyzeProgress = 0;
        AnalyzeStatus   = "Spouštím analýzu…";

        _cts = new CancellationTokenSource();
        var request = new VideoLoraAnalysisRequest(
            VideoPaths: VideoPaths.ToList(),
            Sampling:   new FrameSamplingOptions(FramesPerVideo: Math.Clamp(FramesPerVideo, 1, 200)),
            CaptionStyle: CaptionStyle);

        var progress = new Progress<VideoLoraProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            AnalyzeStatus   = p.StatusLine;
            AnalyzeProgress = p.Total > 0 ? (double)p.Done / p.Total * 100 : 0;
            HasAnalyzeError = p.Stage == VideoLoraStage.Failed;
        }));

        try
        {
            var frames = await _pipeline.AnalyzeAsync(request, progress, _cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var f in frames)
                    Candidates.Add(new CandidateFrameViewModel(f, RecomputeSelectedCount));
                RecomputeSelectedCount();

                if (Candidates.Count > 0)
                    AnalyzeStatus = $"Hotovo — {Candidates.Count} snímků. Odškrtni nevhodné a předej do tréninku.";
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => AnalyzeStatus = "Analýza zrušena.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VideoLoraPane: analýza selhala");
            Dispatcher.UIThread.Post(() => { HasAnalyzeError = true; AnalyzeStatus = $"❌ {ex.Message}"; });
        }
        finally
        {
            IsAnalyzing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelAnalyze()
    {
        try { _cts?.Cancel(); }
        catch (Exception ex) { Log.Warning(ex, "VideoLoraPane: cancel analýzy selhal"); }
    }

    // ── Výběr snímků ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void SelectNone() => SetAllSelected(false);

    private void SetAllSelected(bool value)
    {
        foreach (var c in Candidates) c.IsSelected = value;
        RecomputeSelectedCount();
    }

    private void RecomputeSelectedCount() => SelectedCount = Candidates.Count(c => c.IsSelected);

    // ── Handoff do tréninku ───────────────────────────────────────────────────

    /// <summary>
    /// Předá vybrané snímky do trénovací záložky jako dataset a přepne na ni. Odtud už
    /// uživatel jen vybere base model a spustí standardní trénink.
    /// </summary>
    [RelayCommand]
    private void UseForTraining()
    {
        if (!CanUseForTraining) return;

        var selected = Candidates.Where(c => c.IsSelected).ToList();

        _trainingPane.ClearDatasetCommand.Execute(null);
        foreach (var c in selected)
            _trainingPane.AddDatasetImage(c.FramePath, c.Caption);

        if (!string.IsNullOrWhiteSpace(LoraName))
            _trainingPane.TrainingName = LoraName.Trim();
        _trainingPane.TokenOnlyCaptions = TokenOnlyCaptions;

        _switchToTrainingTab();
    }
}
