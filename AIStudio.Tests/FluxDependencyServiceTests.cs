using AIStudio.Core.Interfaces;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

/// <summary>
/// FluxDependencyService testy se omezují na <c>FindMissing</c> a
/// <c>AreDependenciesPresent</c>, které jsou pure I/O kontrolou.
/// Stahování per se testovat nelze — vyžaduje HTTP server / HF token,
/// to je doménou integration testů.
/// </summary>
public class FluxDependencyServiceTests : IDisposable
{
    private readonly string _tmpDir;

    public FluxDependencyServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "AIStudio.Tests.Flux", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private FluxDependencyService MakeService() =>
        new(Substitute.For<IDownloadService>());

    private void CreateFile(string relPath)
    {
        var full = Path.Combine(_tmpDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Empty);
    }

    // ── FindMissing ──────────────────────────────────────────────────────────

    [Fact]
    public void FindMissing_EmptyDir_AllThreeMissing()
    {
        var svc = MakeService();
        var missing = svc.FindMissing(_tmpDir);

        // Pořadí odpovídá pořadí v Deps array (CLIP-L, VAE, T5)
        missing.Should().HaveCount(3);
        missing.Should().Contain(new[] { "CLIP-L", "T5", "VAE" });
    }

    [Fact]
    public void FindMissing_ClipLInRoot_OnlyTwoMissing()
    {
        CreateFile("clip_l.safetensors");

        var svc = MakeService();
        var missing = svc.FindMissing(_tmpDir);

        missing.Should().HaveCount(2);
        missing.Should().NotContain("CLIP-L");
        missing.Should().Contain("T5");
        missing.Should().Contain("VAE");
    }

    [Fact]
    public void FindMissing_ClipLInSubdir_AlsoDetected()
    {
        // ComfyUI mapuje root i subdir přes extra_model_paths.yaml — soubor v
        // clip/ podadresáři musí být detekován stejně jako v rootu.
        CreateFile(Path.Combine("clip", "clip_l.safetensors"));

        var svc = MakeService();
        var missing = svc.FindMissing(_tmpDir);

        missing.Should().NotContain("CLIP-L");
    }

    [Fact]
    public void FindMissing_VaeInSubdir_Detected()
    {
        CreateFile(Path.Combine("vae", "ae.safetensors"));

        var svc = MakeService();
        var missing = svc.FindMissing(_tmpDir);

        missing.Should().NotContain("VAE");
    }

    [Fact]
    public void FindMissing_AllThreePresent_ReturnsEmpty()
    {
        CreateFile(Path.Combine("clip", "clip_l.safetensors"));
        CreateFile(Path.Combine("clip", "t5xxl_fp8_e4m3fn.safetensors"));
        CreateFile(Path.Combine("vae",  "ae.safetensors"));

        var svc = MakeService();
        var missing = svc.FindMissing(_tmpDir);

        missing.Should().BeEmpty();
    }

    [Fact]
    public void FindMissing_NonExistentDir_ReturnsAllAsMissing()
    {
        var svc = MakeService();
        var missing = svc.FindMissing(Path.Combine(_tmpDir, "does-not-exist"));
        missing.Should().HaveCount(3);
    }

    [Fact]
    public void FindMissing_EmptyPath_ReturnsAllAsMissing()
    {
        var svc = MakeService();
        var missing = svc.FindMissing(string.Empty);
        missing.Should().HaveCount(3);
    }

    // ── AreDependenciesPresent (existující API) ──────────────────────────────

    [Fact]
    public void AreDependenciesPresent_AllInRoot_ReturnsTrue()
    {
        CreateFile("clip_l.safetensors");
        CreateFile("t5xxl_fp8_e4m3fn.safetensors");
        CreateFile("ae.safetensors");

        var svc = MakeService();
        svc.AreDependenciesPresent(_tmpDir).Should().BeTrue();
    }

    [Fact]
    public void AreDependenciesPresent_OneMissing_ReturnsFalse()
    {
        CreateFile(Path.Combine("clip", "clip_l.safetensors"));
        CreateFile(Path.Combine("clip", "t5xxl_fp8_e4m3fn.safetensors"));
        // VAE chybí

        var svc = MakeService();
        svc.AreDependenciesPresent(_tmpDir).Should().BeFalse();
    }

    [Fact]
    public void AreDependenciesPresent_EmptyDir_ReturnsFalse()
    {
        var svc = MakeService();
        svc.AreDependenciesPresent(_tmpDir).Should().BeFalse();
    }
}
