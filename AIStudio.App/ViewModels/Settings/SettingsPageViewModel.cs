using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.App.ViewModels.Settings;

public partial class SettingsPageViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IComfyInstaller  _comfyInstaller;
    private CancellationTokenSource? _saveDebounceCts;
    private CancellationTokenSource? _installCts;

    // ── Vzhled + jazyk ────────────────────────────────────────────────────────
    [ObservableProperty] private AppTheme _selectedTheme;
    [ObservableProperty] private AppLanguage _selectedLanguage;

    // ── Inference ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _useGpu;

    // ── Modely ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _modelsDirectory = string.Empty;

    // ── API tokeny ────────────────────────────────────────────────────────────
    [ObservableProperty] private string _huggingFaceToken = string.Empty;
    [ObservableProperty] private string _civitaiApiKey = string.Empty;

    // ── ComfyUI ───────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComfyInstalled))]
    private string _comfyUiDirectory = string.Empty;

    [ObservableProperty] private int    _comfyUiPort      = 8188;
    [ObservableProperty] private bool   _autoStartComfyUi;
    [ObservableProperty] private string _pythonPath       = string.Empty;

    /// <summary>
    /// True pokud je v <see cref="ComfyUiDirectory"/> reálně rozbalený ComfyUI
    /// (cesta existuje a obsahuje main.py). Řídí UI v ComfyUI sekci — když true,
    /// místo tlačítka „Nainstalovat automaticky" se zobrazí badge „Nainstalováno".
    /// </summary>
    public bool IsComfyInstalled =>
        !string.IsNullOrWhiteSpace(ComfyUiDirectory)
        && Directory.Exists(ComfyUiDirectory)
        && File.Exists(Path.Combine(ComfyUiDirectory, "main.py"));

    // ── ComfyUI auto-instalace ────────────────────────────────────────────────
    [ObservableProperty] private bool   _isInstallingComfyUi;
    [ObservableProperty] private int    _installProgress;
    [ObservableProperty] private string _installStatus       = string.Empty;
    [ObservableProperty] private string _installSpeed        = string.Empty;
    [ObservableProperty] private string _installError        = string.Empty;

    // ── UI stav ───────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _showHfToken;
    [ObservableProperty] private bool _showCivitaiToken;
    [ObservableProperty] private bool _isSaved;

    public IReadOnlyList<AppTheme>    Themes    { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<AppLanguage> Languages { get; } = Enum.GetValues<AppLanguage>();

    /// <summary>Výchozí cesta pro zobrazení v placeholder.</summary>
    public string DefaultModelsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AIStudio", "Models");

    public SettingsPageViewModel(ISettingsService settings, IComfyInstaller comfyInstaller)
    {
        _settings        = settings;
        _comfyInstaller  = comfyInstaller;

        // Načteme hodnoty ze stávajících nastavení
        var s = _settings.Settings;
        _selectedTheme     = s.Theme;
        _selectedLanguage  = s.Language;
        _useGpu            = s.UseGpu;
        _modelsDirectory   = s.ModelsDirectory;
        _huggingFaceToken  = s.HuggingFaceToken;
        _civitaiApiKey     = s.CivitaiApiKey;
        _comfyUiDirectory  = s.ComfyUiDirectory;
        _comfyUiPort       = s.ComfyUiPort;
        _autoStartComfyUi  = s.AutoStartComfyUi;
        _pythonPath        = s.PythonPath;
    }

    // ── Auto-save při každé změně ─────────────────────────────────────────────

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        _settings.Settings.Theme = value;
        ScheduleSave();
    }

    partial void OnSelectedLanguageChanged(AppLanguage value)
    {
        _settings.Settings.Language = value;
        ScheduleSave();
    }

    partial void OnUseGpuChanged(bool value)
    {
        _settings.Settings.UseGpu = value;
        ScheduleSave();
    }

    partial void OnModelsDirectoryChanged(string value)
    {
        _settings.Settings.ModelsDirectory = value;
        ScheduleSave();
    }

    partial void OnHuggingFaceTokenChanged(string value)
    {
        _settings.Settings.HuggingFaceToken = value;
        ScheduleSave();
    }

    partial void OnCivitaiApiKeyChanged(string value)
    {
        _settings.Settings.CivitaiApiKey = value;
        ScheduleSave();
    }

    partial void OnComfyUiDirectoryChanged(string value)
    {
        _settings.Settings.ComfyUiDirectory = value;
        ScheduleSave();
    }

    partial void OnComfyUiPortChanged(int value)
    {
        _settings.Settings.ComfyUiPort = value;
        ScheduleSave();
    }

    partial void OnAutoStartComfyUiChanged(bool value)
    {
        _settings.Settings.AutoStartComfyUi = value;
        ScheduleSave();
    }

    partial void OnPythonPathChanged(string value)
    {
        _settings.Settings.PythonPath = value;
        ScheduleSave();
    }

    /// <summary>
    /// Debounce uložení — čeká 400 ms po poslední změně, pak uloží jednou.
    /// Zabraňuje zbytečnému I/O při psaní tokenu znak po znaku.
    /// </summary>
    private void ScheduleSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts = new CancellationTokenSource();
        var ct = _saveDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, ct);
                await _settings.SaveAsync();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    IsSaved = true;
                    _ = HideSavedBadgeAfterDelayAsync();
                });
            }
            catch (OperationCanceledException) { /* nová změna přišla dřív */ }
        }, ct);
    }

    private async Task HideSavedBadgeAfterDelayAsync()
    {
        await Task.Delay(2500);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSaved = false);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task BrowseModelsFolderAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
            return;

        // Pokusíme se otevřít na aktuální složce
        IStorageFolder? startFolder = null;
        var current = string.IsNullOrWhiteSpace(ModelsDirectory) ? DefaultModelsPath : ModelsDirectory;
        if (Directory.Exists(current))
            startFolder = await win.StorageProvider.TryGetFolderFromPathAsync(current);

        var result = await win.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Vybrat složku pro ukládání modelů",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });

        if (result.Count > 0)
            ModelsDirectory = result[0].Path.LocalPath;
    }

    [RelayCommand]
    private void ResetModelsFolder()
    {
        ModelsDirectory = string.Empty;
    }

    [RelayCommand]
    private void OpenHuggingFacePage()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName  = "https://huggingface.co/settings/tokens",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenCivitaiPage()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName  = "https://civitai.com/user/account",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void ClearHfToken()
    {
        HuggingFaceToken = string.Empty;
    }

    [RelayCommand]
    private void ClearCivitaiKey()
    {
        CivitaiApiKey = string.Empty;
    }

    [RelayCommand]
    private async Task BrowseComfyUiFolderAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
            return;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(ComfyUiDirectory) && Directory.Exists(ComfyUiDirectory))
            startFolder = await win.StorageProvider.TryGetFolderFromPathAsync(ComfyUiDirectory);

        var result = await win.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                   = "Vybrat složku s ComfyUI (kde je main.py)",
            AllowMultiple           = false,
            SuggestedStartLocation  = startFolder
        });

        if (result.Count > 0)
            ComfyUiDirectory = result[0].Path.LocalPath;
    }

    [RelayCommand]
    private async Task BrowsePythonPathAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
            return;

        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Vybrat Python interpreter",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Python")
                    { Patterns = new[] { "python.exe", "python3.exe", "python", "python3" } },
                new FilePickerFileType("Vše") { Patterns = new[] { "*" } }
            }
        });

        if (files is { Count: > 0 })
            PythonPath = files[0].TryGetLocalPath() ?? string.Empty;
    }

    [RelayCommand]
    private void ResetComfyUiDirectory() => ComfyUiDirectory = string.Empty;

    [RelayCommand]
    private void ResetPythonPath() => PythonPath = string.Empty;

    [RelayCommand]
    private void OpenComfyUiReleases()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://github.com/comfyanonymous/ComfyUI/releases",
                UseShellExecute = true
            });
        }
        catch { /* prohlížeč nemusí být k dispozici */ }
    }

    // ── Auto-instalace ComfyUI Portable ───────────────────────────────────────

    /// <summary>
    /// Stáhne ~2 GB ComfyUI Portable (Windows + NVIDIA build) z GitHubu, rozbalí
    /// ho do <c>%LocalAppData%\AIStudio\ComfyUI</c> a po dokončení rovnou vyplní
    /// pole <see cref="ComfyUiDirectory"/> a <see cref="PythonPath"/>.
    /// </summary>
    [RelayCommand]
    private async Task InstallComfyUiAsync()
    {
        if (IsInstallingComfyUi) return;

        var installDir = _comfyInstaller.DefaultInstallDirectory;

        InstallError    = string.Empty;
        InstallStatus   = "Připravuji…";
        InstallSpeed    = string.Empty;
        InstallProgress = 0;
        IsInstallingComfyUi = true;

        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;

        var progress = new Progress<ComfyInstallProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                InstallStatus   = p.Message;
                InstallProgress = p.Percent;

                // Rychlost ukazujeme jen v Downloading fázi a jen pokud máme čísla
                InstallSpeed = p.Stage == ComfyInstallStage.Downloading && p.BytesPerSecond > 0
                    ? FormatSpeed(p.BytesPerSecond)
                    : string.Empty;

                if (p.Stage == ComfyInstallStage.Error && !string.IsNullOrEmpty(p.Error))
                    InstallError = p.Error;
            });
        });

        try
        {
            var (comfyDir, pythonExe) = await _comfyInstaller.InstallAsync(installDir, progress, ct);

            // Po úspěchu rovnou vyplní cesty v Nastavení — uložení proběhne přes
            // partial OnXxxChanged handlery (debounced 400 ms).
            ComfyUiDirectory = comfyDir;
            PythonPath       = pythonExe;

            InstallStatus = "Hotovo! Cesty byly vyplněny v Nastavení.";
            Log.Information("Settings: auto-instalace ComfyUI hotova — comfy={Comfy} | python={Python}",
                            comfyDir, pythonExe);
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "Zrušeno";
            InstallError  = string.Empty;
        }
        catch (Exception ex)
        {
            InstallError  = ex.Message;
            InstallStatus = "Selhalo";
            Log.Error(ex, "Settings: auto-instalace ComfyUI selhala");
        }
        finally
        {
            IsInstallingComfyUi = false;
            InstallSpeed        = string.Empty;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    [RelayCommand]
    private void CancelInstallComfyUi() => _installCts?.Cancel();

    private static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        < 1            => string.Empty,
        < 1_048_576    => $"{bytesPerSec / 1024.0:F0} KB/s",
        _              => $"{bytesPerSec / 1_048_576.0:F1} MB/s"
    };
}
