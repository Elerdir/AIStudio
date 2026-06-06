using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Testy běží bez reálného <c>sd-cli</c>, takže ověřují graceful chování (dostupnost,
/// chyby). Skutečná generace = runtime ověření na stroji s přibaleným sd-cli.
/// </summary>
public sealed class NativeImageGeneratorTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("aistudio_nativegen_").FullName;

    /// <summary>Generátor BEZ sd-cli (nedostupný). Override na neexistující cestu → lokátor ho ignoruje.</summary>
    private NativeImageGenerator MakeUnavailable() =>
        new(outputDirOverride: _tmp, cliPathOverride: Path.Combine(_tmp, "neexistuje-sd-cli"));

    /// <summary>Generátor s „fake" sd-cli (existující soubor) → Status dostupný (ale nespustitelný).</summary>
    private (NativeImageGenerator gen, string cli) MakeWithFakeCli()
    {
        var cli = Path.Combine(_tmp, "sd-cli.exe");
        File.WriteAllText(cli, "not a real exe");
        return (new(outputDirOverride: _tmp, cliPathOverride: cli), cli);
    }

    [Fact]
    public void Status_WithoutCli_ReportsUnavailable_WithReason()
    {
        var status = MakeUnavailable().Status;
        status.IsAvailable.Should().BeFalse();
        status.UnavailableReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Status_WithCli_ReportsAvailable()
    {
        var (gen, _) = MakeWithFakeCli();
        gen.Status.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void IsModelLoaded_InitiallyFalse()
    {
        MakeUnavailable().IsModelLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task LoadModel_WhenUnavailable_Throws()
    {
        var act = async () => await MakeUnavailable().LoadModelAsync("whatever.gguf", NativeGenBackend.Cpu);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Generate_WhenUnavailable_ReturnsFailure_NotThrow()
    {
        var req = new NativeImageRequest("model.gguf", "a cat", "", 512, 512, 20, 7.0, Seed: 1, SamplerName: "euler");
        var result = await MakeUnavailable().GenerateAsync(req);

        result.Success.Should().BeFalse();
        result.FilePaths.Should().BeEmpty();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Generate_WithCliButMissingModel_ReturnsFailure()
    {
        var (gen, _) = MakeWithFakeCli();
        var req = new NativeImageRequest(
            Path.Combine(_tmp, "neexistuje.gguf"), "a cat", "", 512, 512, 20, 7.0, Seed: 1, SamplerName: "euler");

        var result = await gen.GenerateAsync(req);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Model");
    }

    [Fact]
    public async Task Unload_WhenNothingLoaded_DoesNotThrow()
    {
        var act = async () => await MakeUnavailable().UnloadAsync();
        await act.Should().NotThrowAsync();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* ignore */ }
    }
}
