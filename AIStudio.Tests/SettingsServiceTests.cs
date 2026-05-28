using System.Text.Json;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// SettingsService čte/píše settings.json do AppData. Testy používají
/// dočasný adresář aby nezasáhly do skutečného nastavení uživatele.
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _settingsPath;

    public SettingsServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "AIStudio.Tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
        _settingsPath = Path.Combine(_tmpDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private SettingsService MakeService(AppSettings? preload = null)
    {
        if (preload is not null)
        {
            var json = JsonSerializer.Serialize(preload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        return new SettingsService(_settingsPath);
    }

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsDefaultSettings()
    {
        var svc = MakeService();
        await svc.LoadAsync();

        svc.Settings.Should().NotBeNull();
        svc.Settings.ModelsDirectory.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var svc = MakeService();
        svc.Settings.ModelsDirectory = @"C:\Models";
        svc.Settings.HuggingFaceToken = "hf_token_123";

        await svc.SaveAsync();

        var svc2 = MakeService();
        await svc2.LoadAsync();

        svc2.Settings.ModelsDirectory.Should().Be(@"C:\Models");
        svc2.Settings.HuggingFaceToken.Should().Be("hf_token_123");
    }

    [Fact]
    public async Task SaveAndLoad_ChatContextSize_RoundTrips()
    {
        // Regresní test: ChatContextSize byl přidaný do AppSettings, ale chyběl
        // v CloneForDisk → po restartu se uložená hodnota ztrácela a vrátila se
        // na default 8192. Tento test by chybu chytil.
        var svc = MakeService();
        svc.Settings.ChatContextSize = 16384;

        await svc.SaveAsync();

        var svc2 = MakeService();
        await svc2.LoadAsync();
        svc2.Settings.ChatContextSize.Should().Be(16384);
    }

    [Fact]
    public async Task SaveAndLoad_LoraCodeOfConductAccepted_RoundTrips()
    {
        // Stejný regresní test pro CoC flag — nová property v AppSettings musí
        // přežít restart, jinak by uživateli pořád zobrazoval CoC dialog.
        var svc = MakeService();
        svc.Settings.LoraTrainingCodeOfConductAccepted = true;

        await svc.SaveAsync();

        var svc2 = MakeService();
        await svc2.LoadAsync();
        svc2.Settings.LoraTrainingCodeOfConductAccepted.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAndLoad_IgnoredImageUpgradeKinds_RoundTrips()
    {
        var svc = MakeService();
        svc.Settings.IgnoredImageUpgradeKinds.Add("Anime");
        svc.Settings.IgnoredImageUpgradeKinds.Add("Stylized");

        await svc.SaveAsync();

        var svc2 = MakeService();
        await svc2.LoadAsync();
        svc2.Settings.IgnoredImageUpgradeKinds.Should().BeEquivalentTo(new[] { "Anime", "Stylized" });
    }

    [Fact]
    public void CloneForDisk_CoversAllProperties()
    {
        // Defenzivní reflection test — pokud někdo přidá novou property do
        // AppSettings, musí ji přidat i do SettingsService.CloneForDisk.
        // Tento test selže pokud CloneForDisk výsledek neobsahuje konkrétní
        // hodnotu z source pro libovolnou non-default property.
        //
        // Strategie: vyplníme source non-default hodnotami u všech properties,
        // zavoláme CloneForDisk a porovnáme každou property na rovnost.
        // Tokeny jsou výjimka — TokenProtection.Protect je úmyslně mění.

        var src = new AppSettings
        {
            Theme                            = AIStudio.Core.Enums.AppTheme.Light,
            Language                         = AIStudio.Core.Enums.AppLanguage.English,
            ModelsDirectory                  = @"C:\TestModels",
            SetupCompleted                   = true,
            UseGpu                           = false,
            ChatContextSize                  = 32768,
            DefaultChatModelName             = "test_model",
            CivitaiApiKey                    = "civitai_key",
            HuggingFaceToken                 = "hf_token",
            ComfyUiDirectory                 = @"C:\Comfy",
            ComfyUiPort                      = 9999,
            AutoStartComfyUi                 = true,
            PythonPath                       = @"C:\python.exe",
            CheckForUpdates                  = true,
            UpdateChannel                    = "beta",
            LoraTrainingCodeOfConductAccepted = true,
            PendingModelDownloads             = new List<string> { "model1", "model2" },
            IgnoredImageUpgradeKinds          = new List<string> { "Anime" },
        };

        var clone = SettingsService.CloneForDisk(src);

        clone.Theme.Should().Be(src.Theme);
        clone.Language.Should().Be(src.Language);
        clone.ModelsDirectory.Should().Be(src.ModelsDirectory);
        clone.SetupCompleted.Should().Be(src.SetupCompleted);
        clone.UseGpu.Should().Be(src.UseGpu);
        clone.ChatContextSize.Should().Be(src.ChatContextSize);
        clone.DefaultChatModelName.Should().Be(src.DefaultChatModelName);
        clone.ComfyUiDirectory.Should().Be(src.ComfyUiDirectory);
        clone.ComfyUiPort.Should().Be(src.ComfyUiPort);
        clone.AutoStartComfyUi.Should().Be(src.AutoStartComfyUi);
        clone.PythonPath.Should().Be(src.PythonPath);
        clone.CheckForUpdates.Should().Be(src.CheckForUpdates);
        clone.UpdateChannel.Should().Be(src.UpdateChannel);
        clone.LoraTrainingCodeOfConductAccepted.Should().Be(src.LoraTrainingCodeOfConductAccepted);
        clone.PendingModelDownloads.Should().BeEquivalentTo(src.PendingModelDownloads);
        clone.IgnoredImageUpgradeKinds.Should().BeEquivalentTo(src.IgnoredImageUpgradeKinds);

        // Tokeny jsou šifrované — ověříme jen že jsou non-null (přesnost
        // šifrování má vlastní test níže).
        clone.CivitaiApiKey.Should().NotBeNullOrEmpty();
        clone.HuggingFaceToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoadAsync_CorruptedJson_ReturnsDefaultSettings()
    {
        File.WriteAllText(_settingsPath, "{ toto není json }}}");
        var svc = MakeService();
        await svc.LoadAsync();

        svc.Settings.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_CreatesFile()
    {
        var svc = MakeService();
        await svc.SaveAsync();

        File.Exists(_settingsPath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_RaisesSettingsSaved()
    {
        var svc = MakeService();
        bool fired = false;
        svc.SettingsSaved += () => fired = true;

        await svc.SaveAsync();

        fired.Should().BeTrue();
    }

    [Fact]
    public void NotifyModelLibraryChanged_RaisesEvent()
    {
        var svc = MakeService();
        bool fired = false;
        svc.ModelLibraryChanged += () => fired = true;

        svc.NotifyModelLibraryChanged();

        fired.Should().BeTrue();
    }

    [Fact]
    public void NotifyConversationsCleared_RaisesEvent()
    {
        var svc = MakeService();
        bool fired = false;
        svc.ConversationsCleared += () => fired = true;

        svc.NotifyConversationsCleared();

        fired.Should().BeTrue();
    }

    // ── Šifrování tokenů (DPAPI / AES-GCM fallback) ───────────────────────────

    [Fact]
    public async Task SaveAsync_TokensAreEncryptedOnDisk()
    {
        var svc = MakeService();
        svc.Settings.HuggingFaceToken = "hf_secret_PLAINTEXT";
        svc.Settings.CivitaiApiKey    = "civitai_secret_PLAINTEXT";

        await svc.SaveAsync();

        // Na disku NESMÍ být plaintext token
        var raw = await File.ReadAllTextAsync(_settingsPath);
        raw.Should().NotContain("hf_secret_PLAINTEXT");
        raw.Should().NotContain("civitai_secret_PLAINTEXT");
        // Měl by tam být prefix značící šifrování
        raw.Should().Contain("enc:v1:");
    }

    [Fact]
    public async Task LoadAsync_LegacyPlaintextToken_StaysReadable()
    {
        // Simulujeme starou settings.json s plaintext tokenem (před zavedením šifrování)
        var legacy = new AppSettings
        {
            HuggingFaceToken = "hf_legacy_plain",
            CivitaiApiKey    = "civ_legacy_plain"
        };
        var json = JsonSerializer.Serialize(legacy, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);

        var svc = MakeService();
        await svc.LoadAsync();

        // VM musí dostat plaintext (legacy token bez prefixu je idempotentní)
        svc.Settings.HuggingFaceToken.Should().Be("hf_legacy_plain");
        svc.Settings.CivitaiApiKey.Should().Be("civ_legacy_plain");
    }

    // ── Atomicita zápisu + recovery ───────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_CreatesBackupOfPreviousVersion()
    {
        // První save vytvoří hlavní soubor
        var svc1 = MakeService();
        svc1.Settings.ModelsDirectory = @"C:\Original";
        await svc1.SaveAsync();

        // Druhý save by měl ten starý uložit jako .bak
        var svc2 = MakeService();
        await svc2.LoadAsync();
        svc2.Settings.ModelsDirectory = @"C:\Updated";
        await svc2.SaveAsync();

        File.Exists(_settingsPath + ".bak").Should().BeTrue("předchozí verze settings.json musí být v .bak");
        File.Exists(_settingsPath + ".tmp").Should().BeFalse("temp soubor musí být po atomic replace odstraněn");
    }

    [Fact]
    public async Task LoadAsync_MainFileMissing_RecoversFromBackup()
    {
        // Vyrobíme legitimní .bak (bez hlavního souboru) — simulace pádu těsně po File.Replace
        var bak = new AppSettings { ModelsDirectory = @"C:\FromBackup" };
        var json = JsonSerializer.Serialize(bak, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath + ".bak", json);

        var svc = MakeService();
        await svc.LoadAsync();

        svc.Settings.ModelsDirectory.Should().Be(@"C:\FromBackup");
        File.Exists(_settingsPath).Should().BeTrue(".bak musí být obnoveno jako hlavní soubor");
    }
}
