using AIStudio.Core.Models;
using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class SdCliArgsBuilderTests
{
    private static NativeImageRequest Req(
        string sampler = "dpmpp_2m", int batch = 1, string? init = null,
        IReadOnlyList<NativeLora>? loras = null, string? vae = null) =>
        new("model.gguf", "a fox", "blurry", 768, 512, 24, 6.5, Seed: 42,
            SamplerName: sampler, BatchCount: batch, VaePath: vae, Loras: loras,
            InitImagePath: init, Denoise: 0.6);

    private static string ValueAfter(List<string> args, string flag)
    {
        var i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : "";
    }

    [Fact]
    public void Build_Txt2Img_MapsCoreParams()
    {
        var a = SdCliArgsBuilder.Build(Req(), "/out/x.png");

        ValueAfter(a, "-M").Should().Be("img_gen");
        ValueAfter(a, "-m").Should().Be("model.gguf");
        ValueAfter(a, "-p").Should().Be("a fox");
        ValueAfter(a, "-n").Should().Be("blurry");
        ValueAfter(a, "-W").Should().Be("768");
        ValueAfter(a, "-H").Should().Be("512");
        ValueAfter(a, "--steps").Should().Be("24");
        ValueAfter(a, "-s").Should().Be("42");
        ValueAfter(a, "-o").Should().Be("/out/x.png");
        // sampler se mapuje na sd.cpp název
        ValueAfter(a, "--sampling-method").Should().Be("dpm++2m");
    }

    [Fact]
    public void Build_CfgScale_UsesInvariantDecimalPoint()
    {
        // I na lokále s desetinnou čárkou musí být tečka (jinak by sd-cli neparsoval)
        ValueAfter(SdCliArgsBuilder.Build(Req(), "/o.png"), "--cfg-scale").Should().Be("6.5");
    }

    [Fact]
    public void Build_Img2Img_AddsModeInitAndStrength()
    {
        var a = SdCliArgsBuilder.Build(Req(init: "/in/seed.png"), "/o.png");
        ValueAfter(a, "-M").Should().Be("img_gen");      // jeden režim; img2img = přítomnost -i
        ValueAfter(a, "-i").Should().Be("/in/seed.png");
        ValueAfter(a, "--strength").Should().Be("0.6");
    }

    [Fact]
    public void Build_Vae_AddsVaeFlag()
    {
        ValueAfter(SdCliArgsBuilder.Build(Req(vae: "/m/vae.safetensors"), "/o.png"), "--vae")
            .Should().Be("/m/vae.safetensors");
    }

    [Fact]
    public void Build_Lora_InjectsPromptSyntaxAndDir()
    {
        var a = SdCliArgsBuilder.Build(
            Req(loras: new[] { new NativeLora(@"C:\models\loras\styleX.safetensors", 0.8) }), "/o.png");

        ValueAfter(a, "-p").Should().Contain("<lora:styleX:0.8>");
        ValueAfter(a, "--lora-model-dir").Should().Contain("loras");
    }

    [Fact]
    public void Build_Threads_AddedOnlyWhenPositive()
    {
        SdCliArgsBuilder.Build(Req(), "/o.png", threads: 0).Should().NotContain("-t");
        SdCliArgsBuilder.Build(Req(), "/o.png", threads: 8).Should().Contain("-t");
    }

    [Fact]
    public void Build_MissingModel_Throws()
    {
        var bad = Req() with { ModelPath = "" };
        var act = () => SdCliArgsBuilder.Build(bad, "/o.png");
        act.Should().Throw<ArgumentException>();
    }
}
