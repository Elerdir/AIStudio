using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AIStudio.App.ViewModels;
using AIStudio.App.ViewModels.Setup;
using AIStudio.App.Views;
using AIStudio.App.Views.Setup;
using AIStudio.Core.Enums;
using AIStudio.Core.Interfaces;
using AIStudio.Infrastructure.Services;

namespace AIStudio.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        // Serilog — soubor %AppData%\AIStudio\logs\app-YYYY-MM-DD.log (7 dní)
        var logDir = AIStudio.Core.Services.AppPaths.LogsDirectory;
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "app-.log"),
                rollingInterval:        RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("AI Studio starting");

        // Nepozorované Task výjimky (fire-and-forget bloky) by jinak tiše zmizely.
        // Registrujeme až po Serilogu, aby Log.Warning měl kam zapsat.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warning(e.Exception, "UnobservedTaskException — nepozorovaná výjimka v Task");
            e.SetObserved();
        };

        // Zachytit unhandled exception z UI threadu (Avalonia)
        // Nezastaví pád aplikace, ale zapíše do logu dříve než CLR terminuje.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "UIThread.UnhandledException — neošetřená výjimka na UI vlákně");
            // Nezavíráme, app se zavře sama nebo uživatel může pokračovat
        };

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            BootstrapApp();
        }
        catch (Exception ex)
        {
            // Pokud cokoliv při startu selže, nechceme tichou smrt procesu.
            // Zalogujeme chybu na disk a ukážeme uživateli okno s detaily.
            Program.DumpCrash("App.OnFrameworkInitializationCompleted", ex);
            ShowCrashWindow(ex);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Vlastní inicializace aplikace — DI, DB, MainWindow / průvodce.
    /// Vyhozená výjimka je odchycena v OnFrameworkInitializationCompleted.
    /// </summary>
    private void BootstrapApp()
    {
        Log.Information("BootstrapApp begin");

        // DI kontejner
        var services = new ServiceCollection();

        // HTTP klienti — centrální factory, žádný static HttpClient v service třídách
        services.AddHttpClient("civitai", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.Add("User-Agent", "AIStudio/1.0 (https://github.com/aistudio)");
        });
        services.AddHttpClient("huggingface", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.Add("User-Agent", "AIStudio/1.0 (https://github.com/aistudio)");
        });
        services.AddHttpClient("comfy", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("download", c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan; // stahování řídí CancellationToken
            c.DefaultRequestHeaders.Add("User-Agent", "AIStudio/1.0 (.NET; https://github.com/aistudio)");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 10
        });

        // App services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        // Dialog abstrakce (clipboard / file pickers / image preview) — VMs
        // díky tomu nepoužívají Avalonia API přímo a jsou unit-testovatelné.
        services.AddSingleton<IDialogService, AIStudio.App.Services.AvaloniaDialogService>();

        // Infrastructure
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISystemPromptPresetService, SystemPromptPresetService>();
        // GPU detekce — per-platform impl.
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IGpuDetector, WindowsGpuDetector>();
        else if (OperatingSystem.IsMacOS())
            services.AddSingleton<IGpuDetector, MacOsGpuDetector>();
        // System monitor — per-platform impl. Windows: nvidia-smi + WMI;
        // macOS: sysctl + vm_stat + system_profiler.
        if (OperatingSystem.IsWindows())
            services.AddSingleton<ISystemMonitorService, WindowsSystemMonitorService>();
        else if (OperatingSystem.IsMacOS())
            services.AddSingleton<ISystemMonitorService, MacOsSystemMonitorService>();
        services.AddSingleton<ILlamaService, LlamaService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IChatRepository, SqliteChatRepository>();
        services.AddSingleton<IComfyHttpClient, ComfyHttpClient>();
        services.AddSingleton<IComfyService, ComfyService>();
        // ComfyUI installer — per-platform impl.
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IComfyInstaller, WindowsComfyInstaller>();
        else if (OperatingSystem.IsMacOS())
            services.AddSingleton<IComfyInstaller, MacOsComfyInstaller>();
        services.AddSingleton<ILoraLibraryService, LoraLibraryService>();
        services.AddSingleton<IImageRepository, SqliteImageRepository>();
        services.AddSingleton<IHuggingFaceClient, HuggingFaceClient>();
        services.AddSingleton<ICivitaiClient,     CivitaiClient>();
        services.AddSingleton<IModelDiscoveryService, ModelDiscoveryService>();
        services.AddSingleton<IImageIntentParser, ImageIntentParser>();
        services.AddSingleton<IImageModelMatcher, ImageModelMatcher>();
        // Recommender — hybrid wrapper kombinuje curated (rychlé, deterministické)
        // s live Civitai/HF search jako fallback. Curated je registrován jako
        // concrete singleton, hybrid ho dostane ručně v lambdě (vyhneme se
        // konfliktu dvou implementací IImageModelRecommender v DI).
        services.AddSingleton<CuratedImageModelRecommender>();
        services.AddSingleton<IImageModelRecommender>(sp =>
            new HybridImageModelRecommender(
                sp.GetRequiredService<CuratedImageModelRecommender>(),
                sp.GetRequiredService<IModelDiscoveryService>(),
                sp.GetRequiredService<ISettingsService>()));
        // Hybrid keyword detektor pro chat → image gen flow. Žádná latence,
        // žádné LLM volání — bezpečné registrovat jako singleton.
        services.AddSingleton<IChatImageIntentDetector, ChatImageIntentDetector>();
        // Orchestrátor pro chat → image: parser → recommender → matcher → comfy → galerie.
        services.AddSingleton<IChatImageOrchestrator, ChatImageOrchestrator>();
        services.AddSingleton<IFluxDependencyService, FluxDependencyService>();
        // FLUX.1 Kontext — instrukční editace obrázku (přilož + uprav). Orchestrátor
        // si ho vyzvedne (optional dep) a použije pro editaci s referencí; auto-download
        // UNET (~12 GB) + sdílené FLUX závislosti při prvním použití.
        services.AddSingleton<IKontextService, KontextDependencyService>();
        // ESRGAN upscale model (~64 MB) pro „hires fix + upscale" generování —
        // auto-download RealESRGAN_x4plus při prvním zapnutí upscale.
        services.AddSingleton<IUpscaleModelService, UpscaleModelService>();
        // Vision LLM (Stage 3) — chat „vidí" přiložený obrázek a odpoví na otázku.
        // Vlastní Qwen2.5-VL 7B GGUF + mmproj, auto-download (~6 GB) při prvním použití.
        services.AddSingleton<IVisionService, LlamaVisionService>();
        // PuLID-Flux — identita osoby z fotky obličeje bez tréninku. Plně automatická
        // instalace (custom node + insightface + PuLID model + antelopev2) při prvním použití.
        services.AddSingleton<IPuLIDService, PuLIDDependencyService>();

        // LoRA trénink — reuse-uje ComfyUI Python venv (žádný druhý Python pro uživatele).
        // Closure pro python.exe: spočítáme cestu z ComfyInstaller.DetectExisting nad
        // aktuální ComfyUiDirectory v settings — tedy se přepočítává při každém volání,
        // protože uživatel může ComfyUI přeinstalovat za běhu.
        services.AddSingleton<ILoraTrainerDependencyService, LoraTrainerDependencyService>();

        // Helper closure pro resolve Python.exe z ComfyUI venv — sdílený mezi
        // trainer a caption service. Kdyby uživatel přeinstaloval ComfyUI za běhu,
        // cesta se přepočítá při dalším volání.
        Func<IServiceProvider, Func<string?>> makePythonResolver = sp =>
        {
            var installer = sp.GetRequiredService<IComfyInstaller>();
            var settings  = sp.GetRequiredService<ISettingsService>();
            return () =>
            {
                var dir = settings.Settings.ComfyUiDirectory;
                if (string.IsNullOrWhiteSpace(dir)) dir = installer.DefaultInstallDirectory;
                return installer.DetectExisting(dir)?.PythonPath;
            };
        };

        services.AddSingleton<ILoraTrainerService>(sp =>
            new SdScriptsLoraTrainer(
                sp.GetRequiredService<ILoraTrainerDependencyService>(),
                makePythonResolver(sp)));

        services.AddSingleton<ILoraCaptionService>(sp =>
            new BlipCaptionService(
                sp.GetRequiredService<ILoraTrainerDependencyService>(),
                makePythonResolver(sp)));

        // ViewModels — každý dostane ze DI jen vlastní závislosti
        services.AddSingleton<AIStudio.App.ViewModels.Chat.ChatPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.ImageStudio.ImageStudioPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.Models.ModelManagerPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.Lora.LoraLibraryPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.SystemMonitor.SystemPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.Settings.SettingsPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.Setup.FirstRunWizardViewModel>();
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        Services = services.BuildServiceProvider();

        // VMs vytvořené z dat (ChatMessage z DB records) nemají DI ctor; statický
        // accessor jim umožňuje volat dialog/clipboard přes IDialogService bez
        // znalosti Avalonia API. Nastavit musíme až po BuildServiceProvider.
        AIStudio.App.ViewModels.Chat.ChatMessage.DialogService =
            Services.GetRequiredService<IDialogService>();

        // ── Synchronní inicializace — nutně přes Task.Run, jinak deadlock ─────
        // Pozadí: jsme na UI threadu s AvaloniaSynchronizationContextem. Pokud
        // bychom volali rovnou `LoadAsync().GetAwaiter().GetResult()`, await
        // uvnitř (např. `File.ReadAllTextAsync`) by zachytil sync context a chtěl
        // continuation dokončit na UI threadu — který by ale byl zablokovaný
        // tímhle GetResult(). Klasický async-over-sync deadlock.
        // Task.Run pošle volání na threadpool (bez sync contextu), tam continuation
        // doběhne, GetResult() na UI threadu jen čeká na hotový Task. Bezpečné.
        Log.Information("Loading settings…");
        Task.Run(async () =>
        {
            await Services.GetRequiredService<ISettingsService>().LoadAsync();
            await Services.GetRequiredService<IChatRepository>().InitializeAsync();
            await Services.GetRequiredService<IImageRepository>().InitializeAsync();
        }).GetAwaiter().GetResult();
        Log.Information("Settings + DB ready");

        // Aplikuj uložený theme. Pozn.: pro plný light mode by chtělo refaktor
        // všech custom hardcoded barev (#161618 atd.) na DynamicResource — to je
        // další iterace. Pro teď přepneme aspoň ovládací prvky FluentTheme.
        var settings = Services.GetRequiredService<ISettingsService>().Settings;
        ApplyTheme(settings.Theme);
        Services.GetRequiredService<ILocalizationService>().Language = settings.Language;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsSvc = Services.GetRequiredService<ISettingsService>();
            var monitor  = Services.GetRequiredService<ISystemMonitorService>();

            // ── Cleanup handler ───────────────────────────────────────────────
            // Sdílená akce volaná jak při čistém zavření (desktop.Exit), tak
            // při pádu (AppDomain.ProcessExit). Voláme ji idempotentně — díky
            // StopAsync() uvnitř ComfyService je bezpečné volat vícekrát.
            static void DoCleanup(string source)
            {
                Log.Information("App.Cleanup [{Source}]: zahajuji cleanup", source);

                // 1) Zabij ComfyUI — jinak by zůstal zombie na portu
                try
                {
                    App.Services.GetRequiredService<IComfyService>()
                                .StopAsync()
                                .GetAwaiter()
                                .GetResult();
                    Log.Information("App.Cleanup [{Source}]: ComfyUI zastaven", source);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "App.Cleanup [{Source}]: ComfyUI cleanup selhal — zombie proces může zůstat", source);
                }

                // 2) Force-flush SQLite connection pool — WAL checkpoint
                try
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    Log.Information("App.Cleanup [{Source}]: SQLite pool flushed", source);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "App.Cleanup [{Source}]: SQLite pool flush selhal", source);
                }

                // 3) Flush Serilog bufferů (musí být poslední)
                try { Log.Information("App.Cleanup [{Source}]: dokončeno", source); Log.CloseAndFlush(); } catch { }
            }

            // Při zavření okna (normální exit i Alt+F4)
            desktop.Exit += (_, _) => DoCleanup("desktop.Exit");

            // Při pádu nebo Environment.Exit — ProcessExit se volá i při
            // unhandled exception ještě před tím, než CLR terminuje proces.
            // POZOR: handler musí být registrován až PO inicializaci DI (Services),
            // protože DoCleanup přistupuje k Services.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DoCleanup("ProcessExit");

            if (!settings.SetupCompleted)
            {
                // ── První spuštění: zobraz průvodce nastavením ─────────────────
                var wizardVm = Services.GetRequiredService<FirstRunWizardViewModel>();

                var wizard = new FirstRunWizardWindow();
                desktop.MainWindow = wizard;

                wizard.Opened += (_, _) => wizard.Initialize(wizardVm);

                wizard.Closed += (_, _) =>
                {
                    // Po dokončení průvodce otevři hlavní okno
                    try
                    {
                        var mainVm     = Services.GetRequiredService<MainWindowViewModel>();
                        var mainWindow = new MainWindow { DataContext = mainVm };
                        desktop.MainWindow = mainWindow;
                        mainWindow.Show();
                        _ = monitor.StartAsync();
                        _ = Task.Run(() => Services.GetRequiredService<IComfyService>().InitializeAsync());
                        _ = Task.Run(() => TriggerFluxDepsAsync(Services));
                        // Doporučené modely vybrané ve wizardu — stáhne na pozadí
                        _ = Task.Run(() => TriggerPendingDownloadsAsync(Services));
                    }
                    catch (Exception ex)
                    {
                        Program.DumpCrash("App.WizardClosed → MainWindow", ex);
                        ShowCrashWindow(ex);
                    }
                };
            }
            else
            {
                // ── Standardní start ──────────────────────────────────────────
                var mainVm     = Services.GetRequiredService<MainWindowViewModel>();
                var mainWindow = new MainWindow { DataContext = mainVm };
                desktop.MainWindow = mainWindow;
                _ = monitor.StartAsync();
                // ComfyService init po zobrazení okna — dělá HTTP request, nesmí blokovat startup
                _ = Task.Run(() => Services.GetRequiredService<IComfyService>().InitializeAsync());
                // FLUX deps — stahujeme na pozadí pokud models dir obsahuje GGUF modely
                _ = Task.Run(() => TriggerFluxDepsAsync(Services));
                // Pending modely z wizardu nebo z předchozí přerušené session
                _ = Task.Run(() => TriggerPendingDownloadsAsync(Services));
            }
        }
    }

    /// <summary>
    /// Aplikuje theme variant na běžící aplikaci. Voláno při startu (z LoadAsync)
    /// a kdykoliv uživatel změní hodnotu v Nastavení (přes <c>SettingsService</c>).
    ///
    /// Pozn.: Aktuálně funguje jen na FluentTheme controly (combobox, scrollbar,
    /// titulek okna na Windows). Custom hardcoded barvy v naší AppStyles.axaml
    /// (#0D0D0D pozadí, #161618 sidebar, #1C1C1E karty, …) zůstávají tmavé,
    /// takže Light variant je v tuhle chvíli kompromisní. Plný light theme
    /// vyžaduje refactor všech barev na DynamicResource.
    /// </summary>
    // Handler pro odhlášení ze sledování OS tématu (jen AppTheme.System).
    private static EventHandler<Avalonia.Platform.PlatformColorValues>? _osColorHandler;

    public static void ApplyTheme(AppTheme theme)
    {
        var app = Avalonia.Application.Current;
        if (app is null) return;

        // Odhlásíme předchozí OS subscription — jen AppTheme.System ji registruje.
        if (_osColorHandler is not null && app.PlatformSettings is { } ps0)
        {
            ps0.ColorValuesChanged -= _osColorHandler;
            _osColorHandler = null;
        }

        switch (theme)
        {
            case AppTheme.Light:
                app.RequestedThemeVariant = ThemeVariant.Light;
                break;

            case AppTheme.Dark:
                app.RequestedThemeVariant = ThemeVariant.Dark;
                break;

            case AppTheme.System:
                // Nastav okamžitě dle aktuálního OS tématu, pak sleduj změny.
                app.RequestedThemeVariant = ResolveOsVariant(app);
                if (app.PlatformSettings is { } ps)
                {
                    _osColorHandler = (_, colors) =>
                    {
                        var v = colors.ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light
                            ? ThemeVariant.Light : ThemeVariant.Dark;
                        Avalonia.Threading.Dispatcher.UIThread.Post(
                            () => { if (Avalonia.Application.Current is { } a) a.RequestedThemeVariant = v; });
                        Log.Information("App: OS theme change → {Variant}", v);
                    };
                    ps.ColorValuesChanged += _osColorHandler;
                }
                break;
        }

        Log.Information("App: applied theme {Theme}", theme);
    }

    /// <summary>
    /// Spustí kontrolu a případné stahování FLUX závislostí na pozadí.
    /// Trigger podmínka: models adresář je nastaven A obsahuje alespoň jeden .gguf soubor.
    /// Tím se vyhneme zbytečnému stahování ~5 GB pro uživatele, kteří FLUX nepoužívají.
    /// </summary>
    private static async Task TriggerFluxDepsAsync(IServiceProvider sp)
    {
        try
        {
            var settings = sp.GetRequiredService<ISettingsService>().Settings;
            var modelsDir = AIStudio.Core.Services.AppPaths.ResolveModelsDirectory(settings.ModelsDirectory);

            var fluxSvc = sp.GetRequiredService<IFluxDependencyService>();

            if (!fluxSvc.HasGgufModels(modelsDir))
            {
                Log.Debug("TriggerFluxDeps: žádné GGUF modely v {Dir}, přeskakuji", modelsDir);
                return;
            }

            if (fluxSvc.AreDependenciesPresent(modelsDir))
            {
                Log.Debug("TriggerFluxDeps: FLUX závislosti již přítomny v {Dir}", modelsDir);
                return;
            }

            Log.Information("TriggerFluxDeps: spouštím stahování FLUX závislostí na pozadí");
            await fluxSvc.EnsureAsync(modelsDir, settings.HuggingFaceToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TriggerFluxDeps: chyba při stahování FLUX závislostí");
        }
    }

    /// <summary>
    /// Stáhne modely, které uživatel označil ve wizardu (krok 5) — perzistované
    /// jako IDs v <see cref="AppSettings.PendingModelDownloads"/>. Voláno po
    /// otevření hlavního okna jako fire-and-forget task na threadpoolu.
    ///
    /// Robustnost: pokud uživatel zavře aplikaci uprostřed stahování, zbylé
    /// IDs zůstávají v seznamu a pokus se zopakuje při příštím spuštění
    /// (volá se i v else větvi standardního startu).
    /// </summary>
    private static async Task TriggerPendingDownloadsAsync(IServiceProvider sp)
    {
        try
        {
            var settingsSvc = sp.GetRequiredService<ISettingsService>();
            var pending     = settingsSvc.Settings.PendingModelDownloads.ToList();
            if (pending.Count == 0)
            {
                Log.Debug("TriggerPendingDownloads: žádné pending modely");
                return;
            }

            var downloader = sp.GetRequiredService<IDownloadService>();
            var modelsDir  = AIStudio.Core.Services.AppPaths.ResolveModelsDirectory(settingsSvc.Settings.ModelsDirectory);
            Directory.CreateDirectory(modelsDir);

            Log.Information("TriggerPendingDownloads: zahajuji stahování {N} modelů do {Dir}",
                            pending.Count, modelsDir);

            foreach (var id in pending)
            {
                var model = AIStudio.Infrastructure.Services.RecommendedModels.FindById(id);
                if (model is null)
                {
                    Log.Warning("TriggerPendingDownloads: neznámé ID {Id} — odstraňuji", id);
                    settingsSvc.Settings.PendingModelDownloads.Remove(id);
                    continue;
                }

                var destPath = Path.Combine(modelsDir, model.FileName);
                if (File.Exists(destPath))
                {
                    Log.Information("TriggerPendingDownloads: {File} už existuje, přeskakuji", model.FileName);
                    settingsSvc.Settings.PendingModelDownloads.Remove(id);
                    await settingsSvc.SaveAsync();
                    continue;
                }

                var apiToken = model.RequiresHuggingFaceToken
                    ? settingsSvc.Settings.HuggingFaceToken
                    : null;

                Log.Information("TriggerPendingDownloads: stahuji {Name} → {File} ({MB} MB)",
                                model.Name, model.FileName, model.SizeBytes / 1_048_576);

                try
                {
                    await downloader.DownloadFileAsync(
                        url:            model.DownloadUrl,
                        destPath:       destPath,
                        progress:       null,
                        apiToken:       apiToken,
                        expectedSha256: model.Sha256);

                    Log.Information("TriggerPendingDownloads: {Name} hotovo", model.Name);

                    // Po úspěchu odstraň z pending a ulož
                    settingsSvc.Settings.PendingModelDownloads.Remove(id);
                    await settingsSvc.SaveAsync();

                    // Refresh Model Manageru, ať to uživatel hned vidí
                    settingsSvc.NotifyModelLibraryChanged();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "TriggerPendingDownloads: stažení {Name} selhalo, zkusí se znovu při příštím startu", model.Name);
                    // Nesmažeme z pending — retry next time
                }
            }

            // Po zpracování zkus i FLUX deps znovu — možná jsme právě stáhli první GGUF
            await TriggerFluxDepsAsync(sp);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TriggerPendingDownloads: neočekávaná chyba");
        }
    }

    private static ThemeVariant ResolveOsVariant(Avalonia.Application app)
    {
        var colors = app.PlatformSettings?.GetColorValues();
        return colors?.ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light
            ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    /// <summary>
    /// Otevře jednoduché okno se stack tracem chyby. Pokud i tohle selže,
    /// nic víc neuděláme — DumpCrash to už zapsal na disk.
    /// </summary>
    private void ShowCrashWindow(Exception ex)
    {
        try
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var crash = new Window
            {
                Title  = "AI Studio — chyba při startu",
                Width  = 900,
                Height = 600,
                Background = new SolidColorBrush(Color.Parse("#1C1C1E")),
                Content = new ScrollViewer
                {
                    Padding = new Avalonia.Thickness(16),
                    Content = new TextBox
                    {
                        Text             = ex.ToString(),
                        IsReadOnly       = true,
                        AcceptsReturn    = true,
                        TextWrapping     = TextWrapping.Wrap,
                        FontFamily       = new FontFamily("Consolas, Cascadia Mono, monospace"),
                        FontSize         = 12,
                        Foreground       = new SolidColorBrush(Color.Parse("#EBEBF5")),
                        Background       = Brushes.Transparent,
                        BorderThickness  = new Avalonia.Thickness(0),
                    }
                }
            };

            desktop.MainWindow = crash;
            crash.Show();
        }
        catch
        {
            // Když selže i okno, máme aspoň STARTUP_CRASH.txt — viz DumpCrash.
        }
    }
}
