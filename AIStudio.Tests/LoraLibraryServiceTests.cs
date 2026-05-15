using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

/// <summary>
/// LoraLibraryService kombinuje ComfyUI seznam s lokálním skenem. Lokální sken
/// testujeme s reálným adresářem v Temp (deterministické). ComfyUI integraci
/// testujeme přes NSubstitute mock — ověříme jen že se hodnoty mergeují a
/// dedupují podle case-insensitive porovnání.
/// </summary>
public class LoraLibraryServiceTests : IDisposable
{
    private readonly string _tmpDir;

    public LoraLibraryServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "AIStudio.Tests.Lora", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private LoraLibraryService MakeService(bool comfyRunning = false,
                                            IEnumerable<string>? comfyLoras = null)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { ModelsDirectory = _tmpDir });

        var comfy = Substitute.For<IComfyService>();
        comfy.IsRunning.Returns(comfyRunning);
        comfy.GetLorasAsync(Arg.Any<CancellationToken>())
             .Returns((IReadOnlyList<string>)(comfyLoras?.ToList() ?? new List<string>()));

        return new LoraLibraryService(settings, comfy);
    }

    private void CreateLora(string subdir, string name)
    {
        var dir = Path.Combine(_tmpDir, subdir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, name))!);
        File.WriteAllText(Path.Combine(dir, name), string.Empty);
    }

    // ── ScanLocal ────────────────────────────────────────────────────────────

    [Fact]
    public void ScanLocal_NoSubdirs_ReturnsEmpty()
    {
        var svc = MakeService();
        svc.ScanLocal(_tmpDir).Should().BeEmpty();
    }

    [Fact]
    public void ScanLocal_LorasSubdir_FindsFile()
    {
        CreateLora("loras", "anime.safetensors");

        var svc = MakeService();
        var found = svc.ScanLocal(_tmpDir);

        found.Should().ContainSingle().Which.Should().Be("anime.safetensors");
    }

    [Fact]
    public void ScanLocal_BothLoraAndLorasSubdirs_FindsAll()
    {
        CreateLora("lora",  "from-singular.safetensors");
        CreateLora("loras", "from-plural.safetensors");

        var svc = MakeService();
        var found = svc.ScanLocal(_tmpDir);

        found.Should().HaveCount(2);
        found.Should().Contain("from-singular.safetensors");
        found.Should().Contain("from-plural.safetensors");
    }

    [Fact]
    public void ScanLocal_NestedSubdirs_UsesForwardSlashRelativePath()
    {
        // Modely v "loras/anime/style.safetensors" musí být vidět jako "anime/style.safetensors"
        // — ComfyUI LoraLoader takový formát očekává.
        CreateLora("loras", Path.Combine("anime", "style.safetensors"));

        var svc = MakeService();
        var found = svc.ScanLocal(_tmpDir);

        found.Should().ContainSingle().Which.Should().Be("anime/style.safetensors");
    }

    [Fact]
    public void ScanLocal_MultipleExtensions_FindsAll()
    {
        CreateLora("loras", "a.safetensors");
        CreateLora("loras", "b.pt");
        CreateLora("loras", "c.ckpt");
        CreateLora("loras", "ignored.txt");
        CreateLora("loras", "ignored.bin");

        var svc = MakeService();
        var found = svc.ScanLocal(_tmpDir);

        found.Should().BeEquivalentTo(new[] { "a.safetensors", "b.pt", "c.ckpt" });
    }

    [Fact]
    public void ScanLocal_EmptyModelsRoot_ReturnsEmpty()
    {
        var svc = MakeService();
        svc.ScanLocal(string.Empty).Should().BeEmpty();
        svc.ScanLocal("   ").Should().BeEmpty();
    }

    [Fact]
    public void ScanLocal_NonExistentRoot_ReturnsEmpty()
    {
        var svc = MakeService();
        svc.ScanLocal(Path.Combine(_tmpDir, "does-not-exist")).Should().BeEmpty();
    }

    // ── ListAllAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAllAsync_ComfyDown_UsesLocalScanOnly()
    {
        CreateLora("loras", "from-disk.safetensors");

        var svc = MakeService(comfyRunning: false);
        var result = await svc.ListAllAsync();

        result.Should().ContainSingle().Which.Should().Be("from-disk.safetensors");
    }

    [Fact]
    public async Task ListAllAsync_ComfyRunning_MergesWithLocal()
    {
        CreateLora("loras", "local.safetensors");

        var svc = MakeService(comfyRunning: true,
                              comfyLoras: new[] { "comfy-only.safetensors", "shared.safetensors" });
        // Pridej i "shared" do local — ověříme dedup
        CreateLora("loras", "shared.safetensors");

        var result = await svc.ListAllAsync();

        // 3 unikátní: shared, comfy-only, local — abecedně seřazeno
        result.Should().HaveCount(3);
        result.Should().Contain("local.safetensors");
        result.Should().Contain("comfy-only.safetensors");
        result.Should().Contain("shared.safetensors");
    }

    [Fact]
    public async Task ListAllAsync_DuplicatesCaseInsensitive_OnlyOne()
    {
        CreateLora("loras", "Foo.safetensors");

        var svc = MakeService(comfyRunning: true,
                              comfyLoras: new[] { "foo.safetensors" });

        var result = await svc.ListAllAsync();
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListAllAsync_ResultsAreSorted()
    {
        CreateLora("loras", "zebra.safetensors");
        CreateLora("loras", "alpha.safetensors");
        CreateLora("loras", "mango.safetensors");

        var svc = MakeService();
        var result = await svc.ListAllAsync();

        result.Should().Equal("alpha.safetensors", "mango.safetensors", "zebra.safetensors");
    }
}
