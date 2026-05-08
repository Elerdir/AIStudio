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
}
