using AIStudio.Core.Interfaces;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

/// <summary>
/// Testy detekce dostupnosti FLUX.1 Kontext. Stahování (12 GB) netestujeme —
/// jen logiku „je vše na disku?". FLUX závislosti jsou mockované přes
/// <see cref="IFluxDependencyService"/>, UNET zakládáme jako fake soubor v temp dir.
/// </summary>
public sealed class KontextDependencyServiceTests : IDisposable
{
    private const string UnetFile = "flux1-dev-kontext_fp8_scaled.safetensors";

    private readonly string                 _tmpDir;
    private readonly IDownloadService       _downloader = Substitute.For<IDownloadService>();
    private readonly IFluxDependencyService _fluxDeps   = Substitute.For<IFluxDependencyService>();
    private readonly KontextDependencyService _svc;

    public KontextDependencyServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "AIStudio.Tests.Kontext", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
        _svc = new KontextDependencyService(_downloader, _fluxDeps);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private void CreateUnet(string subdir = "unet")
    {
        var dir = Path.Combine(_tmpDir, subdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, UnetFile), "fake unet");
    }

    [Fact]
    public void UnetFileName_IsExpected() =>
        _svc.UnetFileName.Should().Be(UnetFile);

    [Fact]
    public void IsAvailable_NothingPresent_False()
    {
        _fluxDeps.AreDependenciesPresent(_tmpDir).Returns(true);
        _svc.IsAvailable(_tmpDir).Should().BeFalse("chybí UNET");
    }

    [Fact]
    public void IsAvailable_UnetPresentButDepsMissing_False()
    {
        CreateUnet();
        _fluxDeps.AreDependenciesPresent(_tmpDir).Returns(false);
        _svc.IsAvailable(_tmpDir).Should().BeFalse("chybí FLUX závislosti");
    }

    [Fact]
    public void IsAvailable_UnetInSubdirAndDepsPresent_True()
    {
        CreateUnet("unet");
        _fluxDeps.AreDependenciesPresent(_tmpDir).Returns(true);
        _svc.IsAvailable(_tmpDir).Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_UnetInRootAndDepsPresent_True()
    {
        // UNET přímo v Models rootu (ComfyUI vidí přes extra_model_paths root mapping)
        File.WriteAllText(Path.Combine(_tmpDir, UnetFile), "fake unet");
        _fluxDeps.AreDependenciesPresent(_tmpDir).Returns(true);
        _svc.IsAvailable(_tmpDir).Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_NonexistentDir_False() =>
        _svc.IsAvailable(Path.Combine(_tmpDir, "ghost")).Should().BeFalse();

    [Fact]
    public async Task EnsureAsync_AlreadyAvailable_DoesNotDownload()
    {
        CreateUnet();
        _fluxDeps.AreDependenciesPresent(_tmpDir).Returns(true);

        await _svc.EnsureAsync(_tmpDir, hfToken: null, progress: null, ct: default);

        await _downloader.DidNotReceive().DownloadFileAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<AIStudio.Core.Models.DownloadProgressInfo>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
    }
}
