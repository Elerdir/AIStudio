using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Automatické popisky datasetu (BLIP) — spuštění, průběh, zrušení. Partial split
/// z hlavního <see cref="LoraTrainingPaneViewModel"/>; volitelná funkce, běží jen
/// když je k dispozici <c>ILoraCaptionService</c>.
/// </summary>
public partial class LoraTrainingPaneViewModel
{
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

}
