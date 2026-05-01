using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace AIStudio.App.ViewModels.ImageStudio;

public partial class GeneratedImageViewModel : ObservableObject
{
    public string   FilePath  { get; init; } = string.Empty;
    public string   Prompt    { get; init; } = string.Empty;
    public string   Model     { get; init; } = string.Empty;
    public long     Seed      { get; init; }
    public int      Width     { get; init; }
    public int      Height    { get; init; }
    public DateTime Timestamp { get; init; }

    [ObservableProperty] private bool _isSelected;

    private Bitmap? _thumbnail;

    /// <summary>220 px wide thumbnail, loaded on first access and cached.</summary>
    public Bitmap? Thumbnail => _thumbnail ??= LoadThumbnail();

    public string ResolutionLabel => $"{Width} × {Height}";
    public string TimeLabel       => Timestamp.ToString("HH:mm:ss");

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CopyPromptAsync()
    {
        if (string.IsNullOrEmpty(Prompt)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;
        var clipboard = win.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(Prompt);
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{FilePath}\"",
                UseShellExecute = false,
            });
        }
        catch { /* best effort */ }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private Bitmap? LoadThumbnail()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var stream = File.OpenRead(FilePath);
            return Bitmap.DecodeToWidth(stream, 220);
        }
        catch { return null; }
    }
}
