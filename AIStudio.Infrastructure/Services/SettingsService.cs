using System.Text.Json;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Načítá a ukládá <see cref="AppSettings"/> do <c>%AppData%/AIStudio/settings.json</c>.
///
/// **Bezpečnost:** citlivé tokeny (HuggingFace, Civitai) jsou před zápisem
/// šifrované přes <see cref="TokenProtection"/> (DPAPI na Windows, AES-GCM
/// jinde). Při načítání se transparentně dešifrují — VM/UI vrstva pracuje
/// pořád s plaintextem v paměti.
///
/// **Atomicita:** zápis probíhá přes <c>settings.json.tmp</c> + <see cref="File.Replace(string, string, string)"/>,
/// takže pád uprostřed zápisu nezpůsobí korupci. Při čtení automaticky padá
/// zpět na <c>.bak</c> pokud je hlavní soubor nečitelný.
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio", "settings.json");

    private readonly string SettingsPath;
    private readonly string TempPath;
    private readonly string BackupPath;

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public SettingsService() : this(DefaultSettingsPath) { }

    internal SettingsService(string settingsPath)
    {
        SettingsPath = settingsPath;
        TempPath     = settingsPath + ".tmp";
        BackupPath   = settingsPath + ".bak";
    }

    public AppSettings Settings { get; private set; } = new();

    public event Action? ModelLibraryChanged;
    public event Action? SettingsSaved;
    public event Action? ConversationsCleared;

    public void NotifyModelLibraryChanged()  => ModelLibraryChanged?.Invoke();
    public void NotifyConversationsCleared() => ConversationsCleared?.Invoke();

    public async Task LoadAsync()
    {
        // Recovery: pokud poslední save spadl mezi temp→replace, máme bak
        // bez hlavního souboru. Vrátíme bak zpět.
        if (!File.Exists(SettingsPath) && File.Exists(BackupPath))
        {
            try
            {
                File.Move(BackupPath, SettingsPath);
                Log.Warning("SettingsService: hlavní settings.json chyběl, obnoveno ze záložního .bak");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SettingsService: recovery z .bak selhala");
            }
        }

        if (!File.Exists(SettingsPath))
        {
            Settings = new AppSettings();
            return;
        }

        // Pokus o načtení hlavního souboru, fallback na .bak při korupci
        if (TryLoadFile(SettingsPath, out var loaded))
        {
            Settings = loaded;
        }
        else if (File.Exists(BackupPath) && TryLoadFile(BackupPath, out var fromBak))
        {
            Log.Warning("SettingsService: hlavní settings.json je poškozený, načteno ze záložního .bak");
            Settings = fromBak;
        }
        else
        {
            Log.Error("SettingsService: settings.json je poškozený a .bak neexistuje — používám výchozí hodnoty");
            Settings = new AppSettings();
        }

        // Dešifrování tokenů — TokenProtection.Unprotect je idempotentní,
        // takže staré settings.json s plaintexty (legacy) zůstane funkční
        // a zašifruje se až při příštím save.
        Settings.HuggingFaceToken = TokenProtection.Unprotect(Settings.HuggingFaceToken);
        Settings.CivitaiApiKey    = TokenProtection.Unprotect(Settings.CivitaiApiKey);
    }

    /// <summary>
    /// Atomický zápis: serialize → temp file → <see cref="File.Replace"/>.
    /// File.Replace na Windows + POSIX rename je atomická operace, takže
    /// pád uprostřed nezpůsobí poškození hlavního souboru.
    /// </summary>
    public async Task SaveAsync()
    {
        // SemaphoreSlim brání paralelnímu zápisu z více threadů (debounce může
        // překryt další volání). Bez tohoto by se .tmp + Replace mohly zaplést.
        await _saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

            // Pro zápis na disk vytvoříme **kopii** s šifrovanými tokeny.
            // Důležité: nesmíme šifrovat in-place Settings, protože VM/UI
            // čtou plaintext token z téhož objektu během stejné session.
            var snapshot = CloneForDisk(Settings);

            var json = JsonSerializer.Serialize(snapshot, JsonOpts);

            // 1) Zapiš do temp souboru
            await File.WriteAllTextAsync(TempPath, json);

            // 2) Atomicky nahraď hlavní soubor, starý se uloží jako .bak
            if (File.Exists(SettingsPath))
            {
                File.Replace(TempPath, SettingsPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                // Při prvním save ještě hlavní soubor neexistuje, jen přejmenuj
                File.Move(TempPath, SettingsPath);
            }

            SettingsSaved?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SettingsService: SaveAsync selhal — nastavení NEBYLO uloženo");
            // Vyčistit případný osiřelý temp soubor
            try { if (File.Exists(TempPath)) File.Delete(TempPath); } catch { }
            throw; // ať caller ví že save selhal
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Načte JSON z dané cesty a zkusí ho deserializovat na <see cref="AppSettings"/>.
    /// Vrací false pokud cokoliv selže (file IO, JSON syntax, schema mismatch).
    /// </summary>
    private static bool TryLoadFile(string path, out AppSettings loaded)
    {
        try
        {
            var json = File.ReadAllText(path);
            loaded   = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SettingsService: nelze načíst {Path}", path);
            loaded = new AppSettings();
            return false;
        }
    }

    /// <summary>
    /// Mělká kopie <see cref="AppSettings"/> pro zápis na disk — s šifrovanými
    /// tokeny. In-memory <see cref="Settings"/> zůstává nezměněn (plaintext).
    /// </summary>
    private static AppSettings CloneForDisk(AppSettings src)
    {
        return new AppSettings
        {
            Theme                = src.Theme,
            Language             = src.Language,
            ModelsDirectory      = src.ModelsDirectory,
            SetupCompleted       = src.SetupCompleted,
            UseGpu               = src.UseGpu,
            DefaultChatModelName = src.DefaultChatModelName,
            CivitaiApiKey        = TokenProtection.Protect(src.CivitaiApiKey),
            HuggingFaceToken     = TokenProtection.Protect(src.HuggingFaceToken),
            ComfyUiDirectory     = src.ComfyUiDirectory,
            ComfyUiPort          = src.ComfyUiPort,
            AutoStartComfyUi     = src.AutoStartComfyUi,
            PythonPath           = src.PythonPath,
            CheckForUpdates      = src.CheckForUpdates,
            UpdateChannel        = src.UpdateChannel,
        };
    }
}
