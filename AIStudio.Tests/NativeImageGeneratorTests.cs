using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Testy se spouští bez přibalené nativní knihovny (stable-diffusion), takže ověřují
/// <b>graceful fallback</b>: generátor musí hlásit nedostupnost a selhávat čistě, ne padat.
/// Reálná inference je předmět runtime ověření ve Fázi 2.
/// </summary>
public sealed class NativeImageGeneratorTests
{
    private static NativeImageGenerator Make() => new(outputDirOverride: Path.GetTempPath());

    [Fact]
    public void Status_WithoutNativeLib_ReportsUnavailable_WithReason()
    {
        var status = Make().Status;
        status.IsAvailable.Should().BeFalse();
        status.UnavailableReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void IsModelLoaded_InitiallyFalse()
    {
        Make().IsModelLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task LoadModel_WhenUnavailable_Throws()
    {
        var act = async () => await Make().LoadModelAsync("whatever.gguf", NativeGenBackend.Cpu);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Generate_WhenUnavailable_ReturnsFailure_NotThrow()
    {
        var req = new NativeImageRequest(
            "model.gguf", "a cat", "", 512, 512, 20, 7.0, Seed: 1, SamplerName: "euler");

        var result = await Make().GenerateAsync(req);

        result.Success.Should().BeFalse();
        result.FilePaths.Should().BeEmpty();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Unload_WhenNothingLoaded_DoesNotThrow()
    {
        var act = async () => await Make().UnloadAsync();
        await act.Should().NotThrowAsync();
    }
}
