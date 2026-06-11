using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Lora;

/// <summary>
/// Jeden kandidátní snímek z analýzy videa, nabídnutý uživateli k výběru do LoRA datasetu.
/// Karta v mřížce: náhled, zdrojové video + čas, editovatelný popisek, zaškrtávátko.
/// </summary>
public partial class CandidateFrameViewModel : ViewModelBase
{
    private readonly Action? _onSelectionChanged;

    public CandidateFrame Source { get; }
    public string FramePath => Source.FramePath;
    public string TimestampLabel => Source.Timestamp.ToString(@"mm\:ss");
    public string SourceVideoName => Path.GetFileName(Source.SourceVideoPath);

    /// <summary>Popisek (z BLIP/WD14). Editovatelný — projde do datasetu při handoffu.</summary>
    [ObservableProperty] private string _caption;

    /// <summary>Vybrán do datasetu? Default true (uživatel spíš odškrtává nevhodné).</summary>
    [ObservableProperty] private bool _isSelected = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged?.Invoke();

    public CandidateFrameViewModel(CandidateFrame source, Action? onSelectionChanged = null)
    {
        Source              = source;
        _caption            = source.Caption;
        _onSelectionChanged = onSelectionChanged;
        _ = LoadThumbnailAsync();
    }

    private async Task LoadThumbnailAsync()
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(FramePath)) return null;
                    using var stream = File.OpenRead(FramePath);
                    return Bitmap.DecodeToWidth(stream, 240);   // mřížková karta
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "CandidateFrame: thumbnail decode selhal pro {Path}", FramePath);
                    return null;
                }
            });
            if (bmp is not null) Dispatcher.UIThread.Post(() => Thumbnail = bmp);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CandidateFrame: thumbnail task selhala pro {Path}", FramePath);
        }
    }
}
