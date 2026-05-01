using Avalonia;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AIStudio.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // ── Globální záchytná síť pro startup-pády ───────────────────────────
        // Bez nich aplikace umírá tiše: proces zmizí a po sobě nenechá nic.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DumpCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DumpCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            DumpCrash("FATAL in Program.Main", ex);
            throw; // Rider/dotnet zobrazí non-zero exit code
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Zapíše crash report na 3 místa:
    ///   1) %AppData%\AIStudio\logs\STARTUP_CRASH.log  (vždy přístupné)
    ///   2) <složka s .exe>\STARTUP_CRASH.txt          (vidí to uživatel v Exploreru)
    ///   3) Serilog (pokud už byl inicializován)
    /// Voláno z try/catch v Program.Main, App.OnFrameworkInitializationCompleted
    /// a z globálních handlerů. NIKDY nesmí samo vyhodit výjimku.
    /// </summary>
    public static void DumpCrash(string source, Exception? ex)
    {
        var report =
            $"=== AI Studio crash report ============================" + Environment.NewLine +
            $"Time:   {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}" + Environment.NewLine +
            $"Source: {source}" + Environment.NewLine +
            $"OS:     {Environment.OSVersion}" + Environment.NewLine +
            $"CLR:    {Environment.Version}" + Environment.NewLine +
            $"Cwd:    {Environment.CurrentDirectory}" + Environment.NewLine +
            $"Exe:    {Environment.ProcessPath}" + Environment.NewLine +
            $"User:   {Environment.UserName}" + Environment.NewLine +
            Environment.NewLine +
            (ex?.ToString() ?? "(no exception object)") + Environment.NewLine +
            "=========================================================" + Environment.NewLine + Environment.NewLine;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AIStudio", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "STARTUP_CRASH.log"), report);
        }
        catch { /* ignore */ }

        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            File.AppendAllText(Path.Combine(exeDir, "STARTUP_CRASH.txt"), report);
        }
        catch { /* ignore */ }

        try { Log.Fatal(ex, "AI Studio crashed: {Source}", source); } catch { }
    }
}
