using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class ComfyWorkflowBuilderTests
{
    // ── BuildStandard ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildStandard_ContainsRequiredNodes()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "dreamshaper.safetensors", "cat", "blurry",
            512, 512, 20, 7.0, 42);

        wf.Should().ContainKey("3");  // KSampler
        wf.Should().ContainKey("4");  // CheckpointLoaderSimple
        wf.Should().ContainKey("5");  // EmptyLatentImage
        wf.Should().ContainKey("9");  // SaveImage
    }

    [Fact]
    public void BuildStandard_KSampler_HasCorrectSeedAndSteps()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "dog", "ugly",
            1024, 1024, 30, 8.0, 12345);

        var ksampler = (Dictionary<string, object>)wf["3"];
        var inputs   = (Dictionary<string, object>)ksampler["inputs"];

        inputs["seed"].Should().Be(12345L);
        inputs["steps"].Should().Be(30);
        inputs["cfg"].Should().Be(8.0);
    }

    [Fact]
    public void BuildStandard_CheckpointLoaderSimple_HasCorrectName()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "juggernaut_xl.safetensors", "p", "n", 512, 512, 20, 7, 1);

        var node   = (Dictionary<string, object>)wf["4"];
        var inputs = (Dictionary<string, object>)node["inputs"];
        inputs["ckpt_name"].Should().Be("juggernaut_xl.safetensors");
    }

    // ── BuildFlux ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildFlux_ContainsFluxGuidanceNode()
    {
        var wf = ComfyWorkflowBuilder.BuildFlux(
            "flux1-dev.safetensors", "mountain lake",
            1024, 1024, 20, 3.5, 99);

        var guidanceNode = wf.Values
            .Cast<Dictionary<string, object>>()
            .FirstOrDefault(n => n.TryGetValue("class_type", out var t) && t is "FluxGuidance");

        guidanceNode.Should().NotBeNull("BuildFlux must include a FluxGuidance node");
    }

    [Fact]
    public void BuildFlux_KSampler_CfgIsAlwaysOne()
    {
        var wf = ComfyWorkflowBuilder.BuildFlux(
            "flux1-schnell.safetensors", "forest",
            512, 512, 4, 0.0, 1);

        // Find KSampler node
        var ksampler = wf.Values
            .Cast<Dictionary<string, object>>()
            .First(n => n.TryGetValue("class_type", out var t) && t is "KSampler");
        var inputs = (Dictionary<string, object>)ksampler["inputs"];

        // FLUX KSampler must always have cfg=1.0 (guidance handled by FluxGuidance node)
        inputs["cfg"].Should().Be(1.0);
    }

    // ── BuildFluxGguf ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildFluxGguf_UsesUnetLoaderGguf()
    {
        var wf = ComfyWorkflowBuilder.BuildFluxGguf(
            "flux1-dev-Q4_K_M.gguf",
            ComfyWorkflowBuilder.DefaultFluxClipL,
            ComfyWorkflowBuilder.DefaultFluxT5,
            ComfyWorkflowBuilder.DefaultFluxVae,
            "sunrise", 1024, 1024, 20, 3.5, 7);

        var unet = wf.Values
            .Cast<Dictionary<string, object>>()
            .First(n => n.TryGetValue("class_type", out var t) && t is "UnetLoaderGGUF");

        var inputs = (Dictionary<string, object>)unet["inputs"];
        inputs["unet_name"].Should().Be("flux1-dev-Q4_K_M.gguf");
    }

    [Fact]
    public void BuildFluxGguf_HasDualClipLoader()
    {
        var wf = ComfyWorkflowBuilder.BuildFluxGguf(
            "model.gguf",
            "clip_l.safetensors", "t5.safetensors", "ae.safetensors",
            "stars", 512, 512, 4, 0.0, 1);

        var clip = wf.Values
            .Cast<Dictionary<string, object>>()
            .First(n => n.TryGetValue("class_type", out var t) && t is "DualCLIPLoader");

        var inputs = (Dictionary<string, object>)clip["inputs"];
        inputs["type"].Should().Be("flux");
    }

    // ── InjectReferenceImages ─────────────────────────────────────────────────

    [Fact]
    public void InjectReferenceImages_SingleRef_AddsLoadImageAndVaeEncode()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);

        ComfyWorkflowBuilder.InjectReferenceImages(
            wf, emptyLatentKey: "5", ksamplerKey: "3",
            vaeRef: ComfyWorkflowBuilder.StandardVaeRef,
            referenceImageNames: ["ref.png"],
            width: 512, height: 512, strength: 0.7);

        // EmptyLatentImage should be removed
        wf.Should().NotContainKey("5");

        var loadImage = wf.Values
            .Cast<Dictionary<string, object>>()
            .FirstOrDefault(n => n.TryGetValue("class_type", out var t) && t is "LoadImage");
        loadImage.Should().NotBeNull();

        var vaeEncode = wf.Values
            .Cast<Dictionary<string, object>>()
            .FirstOrDefault(n => n.TryGetValue("class_type", out var t) && t is "VAEEncode");
        vaeEncode.Should().NotBeNull();
    }

    [Fact]
    public void InjectReferenceImages_MultipleRefs_AddsLatentBlend()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);

        ComfyWorkflowBuilder.InjectReferenceImages(
            wf, "5", "3",
            ComfyWorkflowBuilder.StandardVaeRef,
            ["ref1.png", "ref2.png", "ref3.png"],
            512, 512, 0.6);

        var blendNodes = wf.Values
            .Cast<Dictionary<string, object>>()
            .Where(n => n.TryGetValue("class_type", out var t) && t is "LatentBlend")
            .ToList();

        // N-1 LatentBlend nodes for N reference images
        blendNodes.Should().HaveCount(2);
    }

    [Fact]
    public void InjectReferenceImages_Empty_DoesNotModifyWorkflow()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);
        var originalCount = wf.Count;

        ComfyWorkflowBuilder.InjectReferenceImages(
            wf, "5", "3",
            ComfyWorkflowBuilder.StandardVaeRef,
            [],  // empty
            512, 512, 0.6);

        wf.Should().HaveCount(originalCount, "empty reference list must not touch the workflow");
    }

    [Fact]
    public void InjectReferenceImages_KSampler_DenoiseSetToOneMinusStrength()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);

        ComfyWorkflowBuilder.InjectReferenceImages(
            wf, "5", "3",
            ComfyWorkflowBuilder.StandardVaeRef,
            ["ref.png"],
            512, 512, strength: 0.7);

        var ksampler = (Dictionary<string, object>)wf["3"];
        var inputs   = (Dictionary<string, object>)ksampler["inputs"];
        ((double)inputs["denoise"]).Should().BeApproximately(0.3, 0.001);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FLUX.1 Schnell",             true)]
    [InlineData("flux1-dev.safetensors",      true)]
    [InlineData("dreamshaper_xl.safetensors", false)]
    [InlineData("juggernaut.safetensors",     false)]
    public void IsFluxModel_CorrectlyIdentifiesFlux(string name, bool expected) =>
        ComfyWorkflowBuilder.IsFluxModel(name).Should().Be(expected);

    [Theory]
    [InlineData("flux1-dev-Q4_K_M.gguf", true)]
    [InlineData("model.safetensors",     false)]
    [InlineData("model.gguf",            true)]
    public void IsGgufModel_CorrectlyIdentifiesGguf(string name, bool expected) =>
        ComfyWorkflowBuilder.IsGgufModel(name).Should().Be(expected);

    [Theory]
    [InlineData("FLUX.1 Schnell", 4,  0.0)]
    [InlineData("flux1-dev",      20, 3.5)]
    public void FluxDefaults_ReturnsCorrectDefaults(string name, int expectedSteps, double expectedGuidance)
    {
        var (steps, guidance) = ComfyWorkflowBuilder.FluxDefaults(name);
        steps.Should().Be(expectedSteps);
        guidance.Should().Be(expectedGuidance);
    }
}
