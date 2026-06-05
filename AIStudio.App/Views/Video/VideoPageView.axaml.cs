using Avalonia.Controls;
using Avalonia.Interactivity;
using AIStudio.App.Controls;

namespace AIStudio.App.Views.Video;

/// <summary>Code-behind video záložky. Logika je ve VM; tady jen přímé ovládání
/// inline přehrávače (play/pauza/znovu), které žije v controlu, ne ve VM.</summary>
public partial class VideoPageView : UserControl
{
    public VideoPageView() => InitializeComponent();

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
        => this.FindControl<VideoPlayerControl>("ResultPlayer")?.TogglePlayPause();

    private void OnReplayClick(object? sender, RoutedEventArgs e)
        => this.FindControl<VideoPlayerControl>("ResultPlayer")?.Replay();
}
