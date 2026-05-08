using System.Text.Json;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

public class SettingsService : ISettingsService
{
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIStudio", "settings.json");

    private readonly string SettingsPath;

    public SettingsService() => SettingsPath = DefaultSettingsPath;

    internal SettingsService(string settingsPath) => SettingsPath = settingsPath;

    public AppSettings Settings { get; private set; } = new();

    public event Action? ModelLibraryChanged;
    public event Action? SettingsSaved;
    public event Action? ConversationsCleared;

    public void NotifyModelLibraryChanged()  => ModelLibraryChanged?.Invoke();
    public void NotifyConversationsCleared() => ConversationsCleared?.Invoke();

    public async Task LoadAsync()
    {
        if (!File.Exists(SettingsPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(SettingsPath);
            Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(SettingsPath, json);
        SettingsSaved?.Invoke();
    }
}
