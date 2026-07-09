using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

/// <summary>
/// Testy portable cesty aktualizace ComfyUI (re-extrakce nejnovějšího buildu).
/// Regresní pokrytí audit nálezu: výsledek <c>UpdateToLatestAsync</c> se musí
/// propsat do settings — nový archiv může mít jiný název kořenové složky a bez
/// přepsání cest by appka dál mířila na starou instalaci.
/// </summary>
public class ComfyUpdateServicePortableTests : IDisposable
{
    private readonly string _root;
    private readonly string _oldComfyDir;

    private readonly IComfyService    _comfy    = Substitute.For<IComfyService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IComfyInstaller  _installer = Substitute.For<IComfyInstaller>();
    private readonly AppSettings      _appSettings = new();

    public ComfyUpdateServicePortableTests()
    {
        // Reálná struktura na disku — služba kontroluje existenci main.py.
        _root = Path.Combine(Path.GetTempPath(), "aistudio_test_" + Guid.NewGuid().ToString("N"));
        _oldComfyDir = Path.Combine(_root, "ComfyUI_windows_portable", "ComfyUI");
        Directory.CreateDirectory(_oldComfyDir);
        File.WriteAllText(Path.Combine(_oldComfyDir, "main.py"), "# comfy");

        _appSettings.ComfyUiDirectory = _oldComfyDir;
        _settings.Settings.Returns(_appSettings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private ComfyUpdateService CreateService() => new(_comfy, _settings, _installer);

    [Fact]
    public async Task UpdatePortable_SavesNewPathsToSettings()
    {
        // Nový archiv má JINOU kořenovou složku (0.25.1 = ..._nvidia_cu126).
        var newComfyDir = Path.Combine(_root, "ComfyUI_windows_portable_nvidia_cu126", "ComfyUI");
        var newPython   = Path.Combine(_root, "ComfyUI_windows_portable_nvidia_cu126", "python_embeded", "python.exe");

        _installer.DetectExisting(Arg.Any<string>()).Returns((_oldComfyDir, "old_python.exe"));
        _installer.UpdateToLatestAsync(Arg.Any<string>(), Arg.Any<IProgress<ComfyInstallProgress>>(), Arg.Any<CancellationToken>())
                  .Returns((newComfyDir, newPython));

        var result = await CreateService().UpdatePortableToLatestAsync();

        result.Success.Should().BeTrue();
        _appSettings.ComfyUiDirectory.Should().Be(newComfyDir);
        _appSettings.PythonPath.Should().Be(newPython);
        await _settings.Received(1).SaveAsync();
    }

    [Fact]
    public async Task UpdatePortable_InstallerThrows_ReturnsFailure_AndKeepsSettings()
    {
        _installer.DetectExisting(Arg.Any<string>()).Returns((_oldComfyDir, "old_python.exe"));
        _installer.UpdateToLatestAsync(Arg.Any<string>(), Arg.Any<IProgress<ComfyInstallProgress>>(), Arg.Any<CancellationToken>())
                  .Returns<Task<(string, string)>>(_ => throw new InvalidOperationException("download selhal"));

        var result = await CreateService().UpdatePortableToLatestAsync();

        result.Success.Should().BeFalse();
        _appSettings.ComfyUiDirectory.Should().Be(_oldComfyDir);   // settings nedotčené
        await _settings.DidNotReceive().SaveAsync();
    }

    [Fact]
    public async Task UpdatePortable_UnusualLayout_FailsWithClearMessage()
    {
        // DetectExisting nic nenajde → neobvyklé umístění → jasná chyba, žádný download.
        _installer.DetectExisting(Arg.Any<string>()).Returns((ValueTuple<string, string>?)null);

        var result = await CreateService().UpdatePortableToLatestAsync();

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("neobvyklém umístění");
        await _installer.DidNotReceive().UpdateToLatestAsync(
            Arg.Any<string>(), Arg.Any<IProgress<ComfyInstallProgress>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePortable_NotInstalled_Fails()
    {
        _appSettings.ComfyUiDirectory = Path.Combine(_root, "neexistuje");

        var result = await CreateService().UpdatePortableToLatestAsync();

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("není nainstalované");
    }

    [Fact]
    public void IsPortableInstall_TrueForNonGitInstall_FalseForGitRepo()
    {
        var svc = CreateService();
        svc.IsPortableInstall(_oldComfyDir).Should().BeTrue();

        Directory.CreateDirectory(Path.Combine(_oldComfyDir, ".git"));
        svc.IsPortableInstall(_oldComfyDir).Should().BeFalse();
    }
}
