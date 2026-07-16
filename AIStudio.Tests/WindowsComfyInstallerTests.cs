using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Testy cross-python guardu ve WindowsComfyInstaller — detekce smíchaného
/// python_embeded po update-extrakci přes instalaci s jinou verzí Pythonu
/// (cu126 = 3.12, aktuální nvidia build = 3.13). Windows-only (třída je na
/// non-Windows Compile-Removed, viz csproj).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsComfyInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _pythonDir;

    public WindowsComfyInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aistudio_test_" + Guid.NewGuid().ToString("N"));
        var portable = Path.Combine(_root, "ComfyUI_windows_portable");
        _pythonDir   = Path.Combine(portable, "python_embeded");
        Directory.CreateDirectory(Path.Combine(portable, "ComfyUI"));
        Directory.CreateDirectory(_pythonDir);
        File.WriteAllText(Path.Combine(portable, "ComfyUI", "main.py"), "# comfy");
        File.WriteAllText(Path.Combine(_pythonDir, "python.exe"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FindMixedPythonDir_TwoVersions_ReturnsDir()
    {
        // Po merge-extrakci: stará 3.12 + nová 3.13 vedle sebe.
        File.WriteAllText(Path.Combine(_pythonDir, "python312.dll"), "");
        File.WriteAllText(Path.Combine(_pythonDir, "python313.dll"), "");

        WindowsComfyInstaller.FindMixedPythonDir(_root).Should().Be(_pythonDir);
    }

    [Fact]
    public void FindMixedPythonDir_SingleVersion_ReturnsNull()
    {
        // Generický python3.dll stub se nepočítá jako druhá verze.
        File.WriteAllText(Path.Combine(_pythonDir, "python3.dll"),   "");
        File.WriteAllText(Path.Combine(_pythonDir, "python313.dll"), "");

        WindowsComfyInstaller.FindMixedPythonDir(_root).Should().BeNull();
    }

    [Fact]
    public void FindMixedPythonDir_NoInstall_ReturnsNull()
    {
        WindowsComfyInstaller.FindMixedPythonDir(
            Path.Combine(_root, "neexistuje")).Should().BeNull();
    }
}
