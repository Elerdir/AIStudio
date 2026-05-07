using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AIStudio.App.ViewModels.Settings;

/// <summary>
/// Read-only viewer pro Serilog soubory v <c>%AppData%\AIStudio\logs\</c>.
/// Hostován ve vlastním okně, otvíraném z Nastavení. Nečte logy live —
/// uživatel musí kliknout Aktualizovat. Pro typický troubleshooting
/// ale stačí, je to one-shot diagnostický nástroj.
/// </summary>
public partial class LogViewerViewModel : ViewModelBase
{
    private static readonly string LogsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio", "logs");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredContent), nameof(LineCount))]
    private string _content = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredContent), nameof(LineCount))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredContent), nameof(LineCount))]
    private LogLevelFilter _selectedLevel = LogLevelFilter.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFilePath))]
    private LogFileItem? _selectedFile;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isCopied;

    public ObservableCollection<LogFileItem> AvailableFiles { get; } = new();

    public IReadOnlyList<LogLevelFilter> Levels { get; } = new[]
    {
        LogLevelFilter.All,
        LogLevelFilter.Information,
        LogLevelFilter.Warning,
        LogLevelFilter.Error,
    };

    public string? SelectedFilePath => SelectedFile?.FullPath;

    public string LineCount => $"{FilteredContent.Count(c => c == '\n') + 1} řádků";

    /// <summary>
    /// Aplikuje úroveň + textový filtr na <see cref="Content"/>. Filtr je per-řádek:
    /// vyhodí řádky, které neobsahují hledaný text nebo jsou nižší úrovně.
    /// Pokračovací řádky (stack trace bez timestamp prefixu) se připojí k poslednímu
    /// matchujícímu řádku, ať se nepřeruší multi-line zápisy chyb.
    /// </summary>
    public string FilteredContent
    {
        get
        {
            if (string.IsNullOrEmpty(Content)) return string.Empty;

            var levelTag = SelectedLevel switch
            {
                LogLevelFilter.Information => "[INF]",
                LogLevelFilter.Warning     => "[WRN]",
                LogLevelFilter.Error       => "[ERR]",
                _                          => null
            };

            var search = SearchText?.Trim() ?? string.Empty;
            var hasSearch = !string.IsNullOrEmpty(search);

            // Pokud žádný filtr aktivní, vrať raw content (rychlé)
            if (levelTag is null && !hasSearch) return Content;

            var sb = new System.Text.StringBuilder(Content.Length);
            var include = false;
            foreach (var line in Content.Split('\n'))
            {
                // Řádek z Serilogu typicky začíná timestampem "2026-05-01 …"
                // Pokračovací řádek (např. stack trace) timestamp nemá — připojíme ho
                // k aktuální vírové větvi, pokud byla zahrnuta.
                var isLogLine = line.Length > 23 &&
                                char.IsDigit(line[0]) && char.IsDigit(line[1]);

                if (isLogLine)
                {
                    var levelOk  = levelTag is null || line.Contains(levelTag, StringComparison.Ordinal);
                    var searchOk = !hasSearch || line.Contains(search, StringComparison.OrdinalIgnoreCase);
                    include = levelOk && searchOk;
                }

                if (include)
                    sb.Append(line).Append('\n');
            }

            return sb.ToString().TrimEnd('\n');
        }
    }

    public LogViewerViewModel()
    {
        RefreshFileList();
        if (AvailableFiles.Count > 0)
        {
            SelectedFile = AvailableFiles[0];
            _ = LoadAsync(SelectedFile.FullPath);
        }
    }

    partial void OnSelectedFileChanged(LogFileItem? value)
    {
        if (value is not null)
            _ = LoadAsync(value.FullPath);
    }

    /// <summary>Najde všechny <c>app-*.log</c> soubory v logs adresáři, řazeno od nejnovějšího.</summary>
    private void RefreshFileList()
    {
        AvailableFiles.Clear();
        if (!Directory.Exists(LogsDir)) return;

        try
        {
            var files = Directory.EnumerateFiles(LogsDir, "*.log")
                                 .Select(p => new FileInfo(p))
                                 .OrderByDescending(f => f.LastWriteTime)
                                 .ToList();
            foreach (var f in files)
                AvailableFiles.Add(new LogFileItem(f.FullName, f.Name, f.Length, f.LastWriteTime));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LogViewer: nelze načíst seznam logů z {Dir}", LogsDir);
        }
    }

    private async Task LoadAsync(string path)
    {
        IsLoading = true;
        try
        {
            // FileShare.ReadWrite — Serilog do souboru zrovna píše, nesmíme ho lockovat
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                 FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            Content = await sr.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            Content = $"Chyba při čtení souboru:\n{ex}";
            Log.Warning(ex, "LogViewer: nelze přečíst {Path}", path);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        RefreshFileList();
        if (SelectedFile is not null)
            await LoadAsync(SelectedFile.FullPath);
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            if (!Directory.Exists(LogsDir))
                Directory.CreateDirectory(LogsDir);
            AIStudio.Infrastructure.Services.PlatformShell.Open(LogsDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LogViewer: open folder selhalo");
        }
    }

    [RelayCommand]
    private async Task CopyToClipboardAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win }) return;

            var clipboard = TopLevel.GetTopLevel(win)?.Clipboard;
            if (clipboard is null) return;

            await clipboard.SetTextAsync(FilteredContent);
            IsCopied = true;
            await Task.Delay(1500);
            IsCopied = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LogViewer: copy to clipboard selhal");
        }
    }
}

/// <summary>Položka v dropdownu výběru log souboru.</summary>
public record LogFileItem(string FullPath, string FileName, long SizeBytes, DateTime LastModified)
{
    public string DisplayName =>
        $"{FileName}  ·  {LastModified:dd. MM. HH:mm}  ·  {FormatSize(SizeBytes)}";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1_024     => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1_024.0:F0} KB",
        _           => $"{bytes / 1_048_576.0:F1} MB"
    };
}

public enum LogLevelFilter
{
    All,
    Information,
    Warning,
    Error,
}
