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
    private readonly ISettingsService        _settings;
    private readonly IComfyInstaller         _comfyInstaller;
    private readonly IChatRepository         _chatRepo;
    private readonly IFluxDependencyService? _fluxDeps;
    private readonly IComfyUpdateService?    _comfyUpdate;
    private readonly IComfyService?          _comfy;
    private readonly INativeImageGenerator?  _nativeGen;
    private CancellationTokenSource? _saveDebounceCts;
    private CancellationTokenSource? _installCts;

    // ── Vzhled + jazyk ────────────────────────────────────────────────────────
    [ObservableProperty] private AppTheme _selectedTheme;
    [ObservableProperty] private AppLanguage _selectedLanguage;

    // ── Inference ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _useGpu;

    /// <summary>
    /// Velikost kontextového okna v tokenech (KV cache). Per-aplikační nastavení,
    /// projeví se po reloadu modelu. Větší ctx = více VRAM, ale delší konverzace.
    /// Hodnota se ukládá do <c>AppSettings.ChatContextSize</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatContextSizeLabel), nameof(IsLargeContextWarning))]
    private int _chatContextSize = 8192;

    /// <summary>True když uživatel vybral hodnotu, která může způsobit OOM na běžné VRAM.</summary>
    public bool IsLargeContextWarning => ChatContextSize > 16384;

    /// <summary>Dostupné předdefinované hodnoty kontextu pro ComboBox v UI.</summary>
    public IReadOnlyList<int> ChatContextSizeOptions { get; } =
        new[] { 2048, 4096, 8192, 16384, 32768, 65536 };

    /// <summary>Lidsky čitelný popis aktuální velikosti kontextu pro UI hint.</summary>
    public string ChatContextSizeLabel => ChatContextSize switch
    {
        < 1_000  => $"{ChatContextSize} tokenů",
        < 10_000 => $"{ChatContextSize / 1_000.0:F1}k tokenů",
        _        => $"{ChatContextSize / 1_000}k tokenů"
    };

    // ── Modely ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _modelsDirectory = string.Empty;

    /// <summary>Složka, jejíž obrázky se zobrazí v záložce Upscale. Prázdná = složka galerie.</summary>
    [ObservableProperty] private string _upscaleSourceDirectory = string.Empty;

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

    // ── Aktualizace ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _checkForUpdates;
    [ObservableProperty] private string _updateChannel = "stable";

    /// <summary>Seznam dostupných update kanálů pro ComboBox v UI.</summary>
    public IReadOnlyList<string> UpdateChannels { get; } = new[] { "stable", "beta", "alpha" };

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

    // ── Smazání chatů — inline confirm ────────────────────────────────────────
    [ObservableProperty] private bool _isConfirmingChatClear;
    [ObservableProperty] private bool _isClearingChats;
    [ObservableProperty] private string _chatClearStatus = string.Empty;

    public IReadOnlyList<AppTheme>    Themes    { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<AppLanguage> Languages { get; } = Enum.GetValues<AppLanguage>();

    /// <summary>Výchozí cesta pro zobrazení v placeholder.</summary>
    public string DefaultModelsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AIStudio", "Models");

    private readonly ILocalizationService? _loc;

    public SettingsPageViewModel(ISettingsService settings,
                                 IComfyInstaller comfyInstaller,
                                 IChatRepository chatRepo,
                                 ILocalizationService? loc = null,
                                 IFluxDependencyService? fluxDeps = null,
                                 IComfyUpdateService? comfyUpdate = null,
                                 IComfyService? comfy = null,
                                 INativeImageGenerator? nativeGen = null)
    {
        _settings        = settings;
        _comfyInstaller  = comfyInstaller;
        _chatRepo        = chatRepo;
        _loc             = loc;
        _fluxDeps        = fluxDeps;
        _comfyUpdate     = comfyUpdate;
        _comfy           = comfy;
        _nativeGen       = nativeGen;

        // Načteme hodnoty ze stávajících nastavení
        var s = _settings.Settings;
        _selectedTheme     = s.Theme;
        _selectedLanguage  = s.Language;
        _useGpu            = s.UseGpu;
        _chatContextSize   = s.ChatContextSize > 0 ? s.ChatContextSize : 8192;
        _modelsDirectory   = s.ModelsDirectory;
        _upscaleSourceDirectory = s.UpscaleSourceDirectory;
        _huggingFaceToken  = s.HuggingFaceToken;
        _civitaiApiKey     = s.CivitaiApiKey;
        _comfyUiDirectory  = s.ComfyUiDirectory;
        _comfyUiPort       = s.ComfyUiPort;
        _autoStartComfyUi  = s.AutoStartComfyUi;
        _pythonPath        = s.PythonPath;
        _checkForUpdates   = s.CheckForUpdates;
        _updateChannel     = string.IsNullOrWhiteSpace(s.UpdateChannel) ? "stable" : s.UpdateChannel;
        _useNativeGenerator = string.Equals(s.ImageGeneratorBackend, "native", StringComparison.OrdinalIgnoreCase);

        RefreshComfyVersion();
    }

    // ── Generátor obrázků (ComfyUI vs vestavěný stable-diffusion.cpp) ──────────

    /// <summary>Preferovat vestavěný generátor (použije se jen když je reálně dostupný).</summary>
    [ObservableProperty] private bool _useNativeGenerator;

    /// <summary>True když je vestavěný generátor reálně dostupný (přibalená nativní knihovna).</summary>
    public bool NativeGeneratorAvailable => _nativeGen?.Status.IsAvailable ?? false;

    /// <summary>Lidský popis stavu vestavěného generátoru pro Nastavení.</summary>
    public string NativeGeneratorStatusLabel => _nativeGen?.Status is { } st
        ? (st.IsAvailable ? $"Dostupný — {st.BackendInfo}" : st.UnavailableReason ?? "nedostupné")
        : "Vestavěný generátor zatím není zapojený (přijde s nativní knihovnou).";

    partial void OnUseNativeGeneratorChanged(bool value)
    {
        _settings.Settings.ImageGeneratorBackend = value ? "native" : "comfyui";
        ScheduleSave();
    }

    // ── Auto-save při každé změně ─────────────────────────────────────────────

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        _settings.Settings.Theme = value;
        AIStudio.App.App.ApplyTheme(value);   // ihned přepni — bez čekání na restart
        ScheduleSave();
    }

    partial void OnSelectedLanguageChanged(AppLanguage value)
    {
        _settings.Settings.Language = value;
        if (_loc is not null) _loc.Language = value;
        ScheduleSave();
    }

    partial void OnUseGpuChanged(bool value)
    {
        _settings.Settings.UseGpu = value;
        ScheduleSave();
    }

    partial void OnChatContextSizeChanged(int value)
    {
        // Clamp pro případ že někdo nastaví ručně přes JSON; UI předává hodnoty
        // z ChatContextSizeOptions, ale safety net pro out-of-band hodnoty.
        var clamped = Math.Clamp(value, 1024, 131072);
        _settings.Settings.ChatContextSize = clamped;
        if (clamped != value) ChatContextSize = clamped;   // re-entrancy chráněna CommunityToolkit setterem
        ScheduleSave();
    }

    partial void OnModelsDirectoryChanged(string value)
    {
        _settings.Settings.ModelsDirectory = value;
        ScheduleSave();
    }

    partial void OnUpscaleSourceDirectoryChanged(string value)
    {
        _settings.Settings.UpscaleSourceDirectory = value;
        ScheduleSave();
    }

    [RelayCommand]
    private async Task PickUpscaleSourceFolderAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
            return;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(UpscaleSourceDirectory) && Directory.Exists(UpscaleSourceDirectory))
            startFolder = await win.StorageProvider.TryGetFolderFromPathAsync(UpscaleSourceDirectory);

        var result = await win.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Vybrat složku s obrázky pro upscale",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });

        if (result.Count > 0)
            UpscaleSourceDirectory = result[0].Path.LocalPath;
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
        RefreshComfyVersion();
        ScheduleSave();
    }

    // ── ComfyUI verze ─────────────────────────────────────────────────────────

    /// <summary>Verze nainstalovaného ComfyUI (z comfyui_version.py). Prázdné = neznámá.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComfyUiVersionLabel), nameof(IsComfyUpToDate),
                              nameof(HasComfyVersion), nameof(CanUpdateComfy), nameof(CanUpdateComfyPortable),
                              nameof(ComfyUpdateUnavailableHint), nameof(HasComfyUpdateUnavailableHint))]
    private string _comfyUiVersion = string.Empty;

    public bool   HasComfyVersion     => !string.IsNullOrEmpty(ComfyUiVersion);
    public string ComfyUiVersionLabel => HasComfyVersion ? $"Verze {ComfyUiVersion}" : "Verze: neznámá";

    /// <summary>Verze, na které je AI Studio ověřené (pinned reference).</summary>
    public string TestedComfyVersion      => AIStudio.Core.Services.ComfyVersion.TestedVersion;
    public string TestedComfyVersionLabel => $"Ověřeno s {TestedComfyVersion}";

    /// <summary>True když běžící verze přesně odpovídá ověřené (zaručeně kompatibilní).</summary>
    public bool IsComfyUpToDate =>
        HasComfyVersion &&
        string.Equals(ComfyUiVersion, TestedComfyVersion, StringComparison.OrdinalIgnoreCase);

    // ── Řízená aktualizace ComfyUI (na ověřenou verzi) ────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdateComfy), nameof(CanUpdateComfyPortable))]
    private bool _isUpdatingComfy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComfyUpdateStatus))]
    private string _comfyUpdateStatus = string.Empty;

    public bool HasComfyUpdateStatus => !string.IsNullOrEmpty(ComfyUpdateStatus);
    public bool HasComfyUpdateUnavailableHint => !string.IsNullOrEmpty(ComfyUpdateUnavailableHint);

    /// <summary>Tlačítko „Sladit s ověřenou verzí" jde nabídnout (git repo + git dostupné + neaktuální).</summary>
    public bool CanUpdateComfy =>
        IsComfyInstalled && HasComfyVersion && !IsComfyUpToDate && !IsUpdatingComfy &&
        _comfyUpdate is not null &&
        _comfyUpdate.IsGitRepo(ComfyUiDirectory) && _comfyUpdate.IsGitAvailable();

    /// <summary>
    /// Tlačítko „Aktualizovat na nejnovější" pro <b>portable</b> (ne-git) instalaci —
    /// stáhne a rozbalí nejnovější portable přes stávající (modely/custom_nodes zůstanou).
    /// </summary>
    public bool CanUpdateComfyPortable =>
        IsComfyInstalled && !IsUpdatingComfy && !IsComfyUpToDate &&
        _comfyUpdate is not null && _comfyUpdate.IsPortableInstall(ComfyUiDirectory);

    /// <summary>Hint, proč nejde aktualizovat, když verze nesedí (chybí git / není git repo).</summary>
    public string ComfyUpdateUnavailableHint
    {
        get
        {
            if (IsComfyUpToDate || !IsComfyInstalled || !HasComfyVersion || _comfyUpdate is null) return string.Empty;
            // Portable (ne-git) instalaci umíme aktualizovat re-extrakcí → místo hintu se ukáže tlačítko.
            if (_comfyUpdate.IsPortableInstall(ComfyUiDirectory)) return string.Empty;
            if (!_comfyUpdate.IsGitRepo(ComfyUiDirectory))
                return "Automatická aktualizace není možná — tahle instalace ComfyUI není git repozitář.";
            if (!_comfyUpdate.IsGitAvailable())
                return "Pro aktualizaci je potřeba Git (git-scm.com) — nenašel jsem ho v systému.";
            return string.Empty;
        }
    }

    [RelayCommand]
    private async Task UpdateComfyAsync()
    {
        if (_comfyUpdate is null || IsUpdatingComfy) return;

        IsUpdatingComfy   = true;
        ComfyUpdateStatus = "Spouštím aktualizaci…";
        var progress = new Progress<string>(m => Dispatcher.UIThread.Post(() => ComfyUpdateStatus = m));
        try
        {
            var res = await _comfyUpdate.UpdateToVersionAsync(TestedComfyVersion, progress);
            ComfyUpdateStatus = res.Message;

            if (res.Success && _comfy is not null)
            {
                ComfyUpdateStatus = res.Message + " Spouštím ComfyUI…";
                try { await _comfy.StartAsync(); ComfyUpdateStatus = $"Hotovo — ComfyUI běží na ověřené verzi {TestedComfyVersion}."; }
                catch (Exception ex) { Log.Warning(ex, "ComfyUpdate: restart ComfyUI selhal"); }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ComfyUpdate: aktualizace selhala");
            ComfyUpdateStatus = "Aktualizace selhala: " + ex.Message;
        }
        finally
        {
            IsUpdatingComfy = false;
            RefreshComfyVersion();
        }
    }

    /// <summary>
    /// Aktualizuje portable (ne-git) ComfyUI na nejnovější build re-extrakcí. Po úspěchu
    /// rovnou spustí ComfyUI. Modely / custom_nodes / output zůstanou zachované.
    /// </summary>
    [RelayCommand]
    private async Task UpdateComfyPortableAsync()
    {
        if (_comfyUpdate is null || IsUpdatingComfy) return;

        IsUpdatingComfy   = true;
        ComfyUpdateStatus = "Spouštím aktualizaci…";
        var progress = new Progress<string>(m => Dispatcher.UIThread.Post(() => ComfyUpdateStatus = m));
        try
        {
            var res = await _comfyUpdate.UpdatePortableToLatestAsync(progress);
            ComfyUpdateStatus = res.Message;

            if (res.Success && _comfy is not null)
            {
                ComfyUpdateStatus = res.Message + " Spouštím ComfyUI…";
                try { await _comfy.StartAsync(); ComfyUpdateStatus = "Hotovo — ComfyUI běží na nejnovější verzi."; }
                catch (Exception ex) { Log.Warning(ex, "ComfyUpdate(portable): restart ComfyUI selhal"); }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ComfyUpdate(portable): aktualizace selhala");
            ComfyUpdateStatus = "Aktualizace selhala: " + ex.Message;
        }
        finally
        {
            IsUpdatingComfy = false;
            RefreshComfyVersion();
        }
    }

    // ── Custom nody (instalují se při startu ComfyUI) ─────────────────────────

    /// <summary>True když je nainstalovaný ComfyUI-VideoHelperSuite (MP4 výstup videa).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoHelperLabel))]
    private bool _videoHelperInstalled;

    public string VideoHelperLabel =>
        VideoHelperInstalled ? "Video (VideoHelperSuite): nainstalováno"
                             : "Video (VideoHelperSuite): doinstaluje se při startu ComfyUI";

    /// <summary>Přečte verzi ComfyUI + stav custom nodů a aktualizuje UI.</summary>
    private void RefreshComfyVersion()
    {
        ComfyUiVersion = AIStudio.Core.Services.ComfyVersion.ReadFromDirectory(ComfyUiDirectory) ?? string.Empty;
        try
        {
            VideoHelperInstalled = IsComfyInstalled
                && _comfyInstaller.IsVideoHelperSuiteInstalled(ComfyUiDirectory);
        }
        catch { VideoHelperInstalled = false; }
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

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.Settings.CheckForUpdates = value;
        Log.Information("Settings: CheckForUpdates → {Value}", value);
        ScheduleSave();
    }

    partial void OnUpdateChannelChanged(string value)
    {
        _settings.Settings.UpdateChannel = string.IsNullOrWhiteSpace(value) ? "stable" : value;
        Log.Information("Settings: UpdateChannel → {Channel}", _settings.Settings.UpdateChannel);
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
                // Trigger FLUX dep check po uložení — detekuje nové gguf modely nebo nový token
                _ = TriggerFluxDepsAfterSaveAsync();
            }
            catch (OperationCanceledException) { /* nová změna přišla dřív */ }
        }, ct);
    }

    private async Task HideSavedBadgeAfterDelayAsync()
    {
        await Task.Delay(2500);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSaved = false);
    }

    private async Task TriggerFluxDepsAfterSaveAsync()
    {
        if (_fluxDeps is null) return;
        try
        {
            var s = _settings.Settings;
            var modelsDir = string.IsNullOrWhiteSpace(s.ModelsDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "AIStudio", "Models")
                : s.ModelsDirectory;

            if (!_fluxDeps.HasGgufModels(modelsDir)) return;
            await _fluxDeps.EnsureAsync(modelsDir, s.HuggingFaceToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SettingsPageViewModel: TriggerFluxDeps selhal");
        }
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
    private void OpenHuggingFacePage() =>
        AIStudio.Infrastructure.Services.PlatformShell.OpenUrl("https://huggingface.co/settings/tokens");

    [RelayCommand]
    private void OpenCivitaiPage() =>
        AIStudio.Infrastructure.Services.PlatformShell.OpenUrl("https://civitai.com/user/account");

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
    private void OpenComfyUiReleases() =>
        AIStudio.Infrastructure.Services.PlatformShell.OpenUrl(
            "https://github.com/comfyanonymous/ComfyUI/releases");

    /// <summary>Otevře samostatné okno s diagnostickým prohlížečem logů.</summary>
    [RelayCommand]
    private void OpenLogViewer()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner }) return;

        var window = new AIStudio.App.Views.Settings.LogViewerWindow();
        window.Show(owner);
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

    // ── Smazání všech chatů ───────────────────────────────────────────────────

    /// <summary>První klik na „Smazat všechny chaty" — zapne confirm panel.</summary>
    [RelayCommand]
    private void BeginChatClearConfirmation() => IsConfirmingChatClear = true;

    /// <summary>Zruš confirmaci — zavře potvrzovací panel bez akce.</summary>
    [RelayCommand]
    private void CancelChatClear()
    {
        IsConfirmingChatClear = false;
        ChatClearStatus       = string.Empty;
    }

    /// <summary>
    /// Vymaže všechny konverzace + zprávy z SQLite. Pak fíruje
    /// <see cref="ISettingsService.NotifyConversationsCleared"/>, aby
    /// ChatPageViewModel synchronizoval svou in-memory ObservableCollection.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmChatClearAsync()
    {
        if (IsClearingChats) return;

        IsClearingChats = true;
        ChatClearStatus = "Mažu konverzace…";

        try
        {
            await _chatRepo.ClearAllConversationsAsync();

            ChatClearStatus       = "Hotovo — všechny konverzace smazány.";
            IsConfirmingChatClear = false;

            // Synchronizace UI: ChatPageViewModel poslouchá tenhle event a
            // vyčistí svou Conversations kolekci. Bez toho by uživatel viděl
            // staré chaty v sidebaru, dokud aplikaci nerestartuje.
            _settings.NotifyConversationsCleared();

            // Status hláška zmizí po pár sekundách — žádný permanent banner.
            _ = HideClearStatusAfterDelayAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ChatClear: SQL operace selhala");
            ChatClearStatus = $"Chyba: {ex.Message}";
        }
        finally
        {
            IsClearingChats = false;
        }
    }

    private async Task HideClearStatusAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (string.IsNullOrEmpty(ChatClearStatus)) return;
        Dispatcher.UIThread.Post(() => ChatClearStatus = string.Empty);
    }

    private static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        < 1            => string.Empty,
        < 1_048_576    => $"{bytesPerSec / 1024.0:F0} KB/s",
        _              => $"{bytesPerSec / 1_048_576.0:F1} MB/s"
    };
}
