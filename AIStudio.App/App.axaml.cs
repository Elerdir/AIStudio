using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using AIStudio.App.ViewModels;
using AIStudio.App.ViewModels.Setup;
using AIStudio.App.Views;
using AIStudio.App.Views.Setup;
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

        // Infrastructure
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISystemMonitorService, SystemMonitorService>();
        services.AddSingleton<ILlamaService, LlamaService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IChatRepository, SqliteChatRepository>();
        services.AddSingleton<IComfyService, ComfyService>();
        services.AddSingleton<IComfyInstaller, ComfyInstaller>();
        services.AddSingleton<IImageRepository, SqliteImageRepository>();

        // ViewModels
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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            var monitor  = (SystemMonitorService)Services.GetRequiredService<ISystemMonitorService>();

            // Při zavření AIStudio zabij ComfyUI (pokud jsme ho my spustili) —
            // jinak by zůstal zombie proces na portu 8188 a další pokus o start
            // by skončil [Errno 10048] bind conflict.
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
            };

            if (!settings.Settings.SetupCompleted)
            {
                // ── První spuštění: zobraz průvodce nastavením ─────────────────
                var wizardVm = new FirstRunWizardViewModel(
                    settings,
                    Services.GetRequiredService<ISystemMonitorService>());

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
