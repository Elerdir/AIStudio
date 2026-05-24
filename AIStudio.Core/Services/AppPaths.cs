namespace AIStudio.Core.Services;

/// <summary>
/// Centralizovaná resolution standardních cest aplikace. Předtím se
/// <c>Path.Combine(Environment.GetFolderPath(SpecialFolder.ApplicationData), "AIStudio", ...)</c>
/// opakovalo na 15+ místech v Infrastructure i App vrstvě. Změna konvence
/// (např. přesun na <c>%LocalAppData%</c>) by vyžadovala scan-and-replace.
///
/// <para>Všechny cesty jsou kompozované z jedné root metody (<see cref="AppDataRoot"/>),
/// takže přesun aplikační složky je jediný atomický change.</para>
///
/// <para>Pozn.: model / image directories podporují override z
/// <c>AppSettings.ModelsDirectory</c> resp. user volby — helpery zde
/// vrací <em>default</em> umístění. Caller je odpovědný za fallback
/// logiku (typicky: pokud settings.ModelsDirectory je prázdný, použij default).</para>
/// </summary>
public static class AppPaths
{
    /// <summary>Root složka aplikace (<c>%AppData%/AIStudio</c> na Windows).</summary>
    public static string AppDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio");

    /// <summary>Logy (<c>%AppData%/AIStudio/logs</c>).</summary>
    public static string LogsDirectory => Path.Combine(AppDataRoot, "logs");

    /// <summary>SQLite konverzací (<c>%AppData%/AIStudio/conversations.db</c>).</summary>
    public static string ConversationsDbPath => Path.Combine(AppDataRoot, "conversations.db");

    /// <summary>SQLite obrázků (<c>%AppData%/AIStudio/images.db</c>).</summary>
    public static string ImagesDbPath => Path.Combine(AppDataRoot, "images.db");

    /// <summary>Settings JSON (<c>%AppData%/AIStudio/settings.json</c>).</summary>
    public static string SettingsFile => Path.Combine(AppDataRoot, "settings.json");

    /// <summary>System prompt presets JSON (existing file name — neměnit kvůli kompatibilitě).</summary>
    public static string SystemPromptPresetsFile => Path.Combine(AppDataRoot, "prompt-presets.json");

    /// <summary>Default modelů (lze přepsat v <c>AppSettings.ModelsDirectory</c>).</summary>
    public static string DefaultModelsDirectory => Path.Combine(AppDataRoot, "Models");

    /// <summary>Default galerie vygenerovaných obrázků.</summary>
    public static string DefaultImagesDirectory => Path.Combine(AppDataRoot, "Images");

    /// <summary>Crash dump file pro startup chyby.</summary>
    public static string StartupCrashFile => Path.Combine(AppDataRoot, "STARTUP_CRASH.txt");

    /// <summary>ComfyUI PID file pro lifecycle tracking.</summary>
    public static string ComfyPidFile => Path.Combine(AppDataRoot, "comfy.pid");

    /// <summary>
    /// Vrátí buď override z user settings, nebo default models directory.
    /// Helper, který v jednom místě řeší fallback ze stringového nastavení.
    /// </summary>
    public static string ResolveModelsDirectory(string? userOverride) =>
        string.IsNullOrWhiteSpace(userOverride) ? DefaultModelsDirectory : userOverride;
}
