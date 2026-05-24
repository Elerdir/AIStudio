using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Serilog;
using AIStudio.App.Views.Chat;
using AIStudio.Core.Interfaces;

namespace AIStudio.App.Services;

/// <summary>
/// Avalonia implementace <see cref="IDialogService"/>. Veškerou interakci s
/// <c>Avalonia.Application.Current</c>, <c>TopLevel</c>, <c>StorageProvider</c>
/// drží na jednom místě, takže VMs můžou používat čistý interface.
///
/// <para>Pokud není MainWindow dostupné (např. při startu před wizardem
/// dokončením), metody tiše skončí (vrátí null / nedělají nic) místo hození.
/// Toto chování je záměrné — UI dialog volaný v okamžiku, kdy okno není
/// dostupné, je edge case, který by neměl crashnout aplikaci.</para>
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
    public async Task SetClipboardTextAsync(string text)
    {
        var win = GetMainWindow();
        if (win is null) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(win)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AvaloniaDialogService: SetClipboardTextAsync selhalo");
        }
    }

    public async Task<string?> SaveFileAsync(
        string title, string suggestedFileName, string defaultExtension,
        IReadOnlyList<FileFilter> filters)
    {
        var sp = GetStorageProvider();
        if (sp is null) return null;

        try
        {
            var result = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = title,
                SuggestedFileName = suggestedFileName,
                DefaultExtension  = defaultExtension,
                FileTypeChoices   = filters.Select(f => new FilePickerFileType(f.Label)
                {
                    Patterns = f.Patterns.ToArray(),
                }).ToArray(),
            });
            return result?.Path.LocalPath;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AvaloniaDialogService: SaveFileAsync selhalo");
            return null;
        }
    }

    public async Task<string?> OpenFileAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        var sp = GetStorageProvider();
        if (sp is null) return null;

        try
        {
            var result = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title           = title,
                AllowMultiple   = false,
                FileTypeFilter  = filters.Select(f => new FilePickerFileType(f.Label)
                {
                    Patterns = f.Patterns.ToArray(),
                }).ToArray(),
            });
            return result.Count > 0 ? result[0].Path.LocalPath : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AvaloniaDialogService: OpenFileAsync selhalo");
            return null;
        }
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        var sp = GetStorageProvider();
        if (sp is null) return null;

        try
        {
            var result = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title         = title,
                AllowMultiple = false,
            });
            return result.Count > 0 ? result[0].Path.LocalPath : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AvaloniaDialogService: OpenFolderAsync selhalo");
            return null;
        }
    }

    public void ShowImagePreview(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return;

        try
        {
            var win  = GetMainWindow();
            var zoom = new ImageZoomWindow();
            zoom.Load(imagePath);
            if (win is not null) zoom.Show(win);
            else zoom.Show();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AvaloniaDialogService: ShowImagePreview selhalo");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Window? GetMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }
                ? w : null;

    private static IStorageProvider? GetStorageProvider()
    {
        var win = GetMainWindow();
        return win is null ? null : TopLevel.GetTopLevel(win)?.StorageProvider;
    }
}
