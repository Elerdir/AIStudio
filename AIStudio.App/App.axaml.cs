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
using AIStudio.App.Services;
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
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIStudio", "logs");
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

        // Infrastructure
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISystemMonitorService, SystemMonitorService>();
        services.AddSingleton<ILlamaService, LlamaService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IChatRepository, SqliteChatRepository>();
        services.AddSingleton<IComfyService, ComfyService>();
        services.AddSingleton<IComfyInstaller, ComfyInstaller>();
        services.AddSingleton<IImageRepository, SqliteImageRepository>();
        services.AddSingleton<IHuggingFaceClient, HuggingFaceClient>();
        services.AddSingleton<ICivitaiClient,     CivitaiClient>();
        services.AddSingleton<IModelDiscoveryService, ModelDiscoveryService>();
        services.AddSingleton<IImageIntentParser, ImageIntentParser>();
        services.AddSingleton<IImageModelMatcher, ImageModelMatcher>();

        // ViewModels — každý dostane ze DI jen vlastní závislosti
        services.AddSingleton<AIStudio.App.ViewModels.Chat.ChatPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.ImageStudio.ImageStudioPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.Models.ModelManagerPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.SystemMonitor.SystemPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.Settings.SettingsPageViewModel>();
        services.AddSingleton<AIStudio.App.ViewModels.Setup.FirstRunWizardViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        Services = services.BuildServiceProvider();

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
        ApplyTheme(Services.GetRequiredService<ISettingsService>().Settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            var monitor  = (SystemMonitorService)Services.GetRequiredService<ISystemMonitorService>();

            // Při zavření AIStudio:
            //   1) Zabij ComfyUI (pokud jsme ho my spustili) — jinak by zůstal zombie
            //      proces na portu 8188 a další pokus o start by skončil [Errno 10048].
            //   2) Force-flushni SQLite connection pool — Microsoft.Data.Sqlite drží
            //      physical connections v poolu i po `using` bloku a WAL checkpoint
            //      se provede až při jejich fyzickém uzavření. Bez tohoto by mohly
            //      poslední transakce uvíznout v db-wal a nebýt commitnuté.
            //   3) Flushni Serilog buffery, ať máme kompletní log i při kill.
            desktop.Exit += (_, _) =>
            {
                try
                {
                    Services.GetRequiredService<IComfyService>()
                            .StopAsync()
                            .GetAwaiter()
                            .GetResult();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "App.Exit: ComfyUI cleanup selhal — zombie proces může zůstat");
                }

                try
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    Log.Information("App.Exit: SQLite pool flushed");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "App.Exit: SQLite pool flush selhal — riziko ztráty posledních zpráv");
                }

                try { Log.CloseAndFlush(); } catch { /* nemůžeme udělat víc */ }
            };

            if (!settings.Settings.SetupCompleted)
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
    public static void ApplyTheme(AppTheme theme)
    {
        if (Avalonia.Application.Current is null) return;

        // Pozn.: AppTheme.System → Dark, ne Default. Aplikace má hard-coded dark
        // barvy v AppStyles.axaml (#0D0D0D, #1C1C1E, …); když uživatel běží
        // Windows v Light módu, FluentTheme by jinak ze System udělal Light
        // variant a TextBoxy/ComboBoxy by byly bílé na našem tmavém pozadí.
        // Light variant vyřešíme až plným DynamicResource refactorem.
        Avalonia.Application.Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light  => ThemeVariant.Light,
            AppTheme.Dark   => ThemeVariant.Dark,
            AppTheme.System => ThemeVariant.Dark,
            _               => ThemeVariant.Dark,
        };

        Log.Information("App: applied theme variant {Theme}", theme);
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
