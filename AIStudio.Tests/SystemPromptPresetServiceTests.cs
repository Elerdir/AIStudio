using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// SystemPromptPresetService kombinuje hardcoded builtin presety s uživatelskými
/// uloženými v JSON souboru. Testy používají dočasný adresář, takže neovlivní
/// skutečný profil uživatele.
/// </summary>
public class SystemPromptPresetServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _filePath;

    public SystemPromptPresetServiceTests()
    {
        _tmpDir   = Path.Combine(Path.GetTempPath(), "AIStudio.Tests.Presets", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
        _filePath = Path.Combine(_tmpDir, "prompt-presets.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private SystemPromptPresetService MakeService() => new(_filePath);

    // ── Builtin ───────────────────────────────────────────────────────────────

    [Fact]
    public void BuiltInPresets_ContainsAllSixDefaults()
    {
        var svc = MakeService();
        svc.BuiltInPresets.Should().HaveCount(6);
        svc.BuiltInPresets.Select(p => p.Name).Should().Contain(new[]
        {
            "Asistent", "Editor", "Kreativní psaní", "Programátor", "Brainstorm", "Bez instrukcí"
        });
    }

    [Fact]
    public void BuiltInPresets_AllMarkedAsBuiltIn()
    {
        var svc = MakeService();
        svc.BuiltInPresets.Should().OnlyContain(p => p.IsBuiltIn);
    }

    [Fact]
    public async Task LoadAll_NoCustomFile_ReturnsOnlyBuiltin()
    {
        var svc = MakeService();
        var all = await svc.LoadAllAsync();
        all.Should().HaveCount(svc.BuiltInPresets.Count);
        all.Should().OnlyContain(p => p.IsBuiltIn);
    }

    // ── Custom: save + load ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveCustom_NewPreset_AppearsInLoadAll()
    {
        var svc = MakeService();
        var custom = new SystemPromptPreset("Můj asistent", "Buď stručný a milý.");

        await svc.SaveCustomAsync(custom);

        var all = await svc.LoadAllAsync();
        all.Should().HaveCount(svc.BuiltInPresets.Count + 1);
        all.Last().Name.Should().Be("Můj asistent");
        all.Last().IsBuiltIn.Should().BeFalse();
    }

    [Fact]
    public async Task SaveCustom_PersistsAcrossInstances()
    {
        var svc1 = MakeService();
        await svc1.SaveCustomAsync(new SystemPromptPreset("Test", "Obsah"));

        var svc2 = MakeService();   // Nová instance, čte ze souboru
        var all = await svc2.LoadAllAsync();
        all.Any(p => p.Name == "Test").Should().BeTrue();
    }

    [Fact]
    public async Task SaveCustom_SameName_OverwritesExisting()
    {
        var svc = MakeService();
        await svc.SaveCustomAsync(new SystemPromptPreset("Můj preset", "První verze"));
        await svc.SaveCustomAsync(new SystemPromptPreset("Můj preset", "Druhá verze"));

        var all = await svc.LoadAllAsync();
        all.Where(p => p.Name == "Můj preset").Should().ContainSingle()
            .Which.Prompt.Should().Be("Druhá verze");
    }

    [Fact]
    public async Task SaveCustom_ForceIsBuiltInFalseEvenIfPassedTrue()
    {
        // Defenzivní — preset s IsBuiltIn=true musí po průchodu service mít IsBuiltIn=false
        var svc = MakeService();
        var sneaky = new SystemPromptPreset("Custom", "Obsah", IsBuiltIn: true);

        await svc.SaveCustomAsync(sneaky);
        var all = await svc.LoadAllAsync();
        all.Single(p => p.Name == "Custom").IsBuiltIn.Should().BeFalse();
    }

    // ── Validace ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveCustom_ConflictsWithBuiltin_Throws()
    {
        var svc = MakeService();
        var conflict = new SystemPromptPreset("Asistent", "Snažím se přepsat builtin.");

        await svc.Invoking(s => s.SaveCustomAsync(conflict))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*Asistent*");
    }

    [Fact]
    public async Task SaveCustom_EmptyName_Throws()
    {
        var svc = MakeService();
        var bad = new SystemPromptPreset(string.Empty, "Bez jména");

        await svc.Invoking(s => s.SaveCustomAsync(bad))
                 .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SaveCustom_Null_Throws()
    {
        var svc = MakeService();
        await svc.Invoking(s => s.SaveCustomAsync(null!))
                 .Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteCustom_ExistingPreset_ReturnsTrueAndRemoves()
    {
        var svc = MakeService();
        await svc.SaveCustomAsync(new SystemPromptPreset("To delete", "Obsah"));

        var deleted = await svc.DeleteCustomAsync("To delete");

        deleted.Should().BeTrue();
        var all = await svc.LoadAllAsync();
        all.Should().NotContain(p => p.Name == "To delete");
    }

    [Fact]
    public async Task DeleteCustom_NonExistent_ReturnsFalse()
    {
        var svc = MakeService();
        var deleted = await svc.DeleteCustomAsync("Neexistuje");
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteCustom_Builtin_ReturnsFalse()
    {
        // Builtin nelze smazat, jinak by uživatel přišel o výchozí stav
        var svc = MakeService();
        var deleted = await svc.DeleteCustomAsync("Asistent");

        deleted.Should().BeFalse();
        var all = await svc.LoadAllAsync();
        all.Should().Contain(p => p.Name == "Asistent");
    }

    [Fact]
    public async Task DeleteCustom_CaseInsensitive()
    {
        var svc = MakeService();
        await svc.SaveCustomAsync(new SystemPromptPreset("MujPreset", "obs"));

        var deleted = await svc.DeleteCustomAsync("mujpreset");
        deleted.Should().BeTrue();
    }

    // ── Corrupted file recovery ───────────────────────────────────────────────

    [Fact]
    public async Task LoadAll_CorruptedJson_FallsBackToBuiltin()
    {
        await File.WriteAllTextAsync(_filePath, "{ this is { not valid json");
        var svc = MakeService();

        var all = await svc.LoadAllAsync();
        all.Should().HaveCount(svc.BuiltInPresets.Count);
        all.Should().OnlyContain(p => p.IsBuiltIn);
    }
}
