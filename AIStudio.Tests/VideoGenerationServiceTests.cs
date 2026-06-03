using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

public class VideoGenerationServiceTests
{
    private static VideoGenerationRequest SampleRequest(WanVideoModel? model = null) =>
        new(model ?? WanModels.T2V_1_3B, "a fox running", 832, 480, 33, 30, 6.0, Seed: 1);

    [Fact]
    public async Task Generate_ComfyNotRunning_Fails()
    {
        var comfy = Substitute.For<IComfyService>();
        comfy.IsRunning.Returns(false);

        var svc = new VideoGenerationService(
            comfy, Substitute.For<IWanDependencyService>(),
            Substitute.For<IImageRepository>(), Substitute.For<ISettingsService>(),
            modelsDirOverride: "x");

        var res = await svc.GenerateAsync(SampleRequest());

        res.Success.Should().BeFalse();
        res.ErrorMessage.Should().Contain("ComfyUI");
    }

    [Fact]
    public async Task Generate_MissingDeps_ReturnsMissingList_DoesNotQueue()
    {
        var comfy = Substitute.For<IComfyService>();
        comfy.IsRunning.Returns(true);

        var wan = Substitute.For<IWanDependencyService>();
        wan.FindMissing(Arg.Any<string>(), Arg.Any<WanVideoModel>())
           .Returns(new[] { "VAE", "Video model" });

        var svc = new VideoGenerationService(
            comfy, wan, Substitute.For<IImageRepository>(), Substitute.For<ISettingsService>(),
            modelsDirOverride: "x");

        var res = await svc.GenerateAsync(SampleRequest());

        res.Success.Should().BeFalse();
        res.MissingDependencies.Should().Contain("VAE").And.Contain("Video model");
        await comfy.DidNotReceive().QueuePromptAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Generate_ImageToVideo_WithoutStartImage_Fails()
    {
        var comfy = Substitute.For<IComfyService>();
        comfy.IsRunning.Returns(true);
        var wan = Substitute.For<IWanDependencyService>();
        wan.FindMissing(Arg.Any<string>(), Arg.Any<WanVideoModel>()).Returns(Array.Empty<string>());

        var svc = new VideoGenerationService(
            comfy, wan, Substitute.For<IImageRepository>(), Substitute.For<ISettingsService>(),
            modelsDirOverride: "x");

        // i2v model, ale StartImagePath = null → musí selhat dřív, než cokoliv zařadí
        var res = await svc.GenerateAsync(SampleRequest(WanModels.I2V_480P_14B));

        res.Success.Should().BeFalse();
        res.ErrorMessage.Should().Contain("vstupní obrázek");
        await comfy.DidNotReceive().QueuePromptAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }
}
