using AIStudio.Core.Models;
using AIStudio.Core.Services;
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

    // ── BuildFluxKontext (instrukční editace) ─────────────────────────────────

    private static Dictionary<string, object> KontextWf() =>
        ComfyWorkflowBuilder.BuildFluxKontext(
            "flux1-dev-kontext_fp8_scaled.safetensors",
            "clip_l.safetensors", "t5xxl_fp8.safetensors", "ae.safetensors",
            "input.png", "add a red hat", steps: 20, guidance: 2.5, seed: 42);

    private static Dictionary<string, object> FindNode(Dictionary<string, object> wf, string classType) =>
        wf.Values.Cast<Dictionary<string, object>>()
          .First(n => n.TryGetValue("class_type", out var t) && (string)t == classType);

    [Fact]
    public void BuildFluxKontext_HasReferenceLatentAndKontextScaleNodes()
    {
        var wf = KontextWf();

        wf.Values.Cast<Dictionary<string, object>>()
          .Should().Contain(n => (string)n["class_type"] == "ReferenceLatent")
          .And.Contain(n => (string)n["class_type"] == "FluxKontextImageScale")
          .And.Contain(n => (string)n["class_type"] == "UNETLoader");
    }

    [Fact]
    public void BuildFluxKontext_ReferenceLatent_WiredToTextEncodeAndVaeEncode()
    {
        var wf  = KontextWf();
        var rl  = (Dictionary<string, object>)FindNode(wf, "ReferenceLatent")["inputs"];

        // conditioning ← CLIPTextEncode (instrukce), latent ← VAEEncode (reference)
        var cond   = (object[])rl["conditioning"];
        var latent = (object[])rl["latent"];

        ((string)((Dictionary<string, object>)wf[(string)cond[0]])["class_type"]).Should().Be("CLIPTextEncode");
        ((string)((Dictionary<string, object>)wf[(string)latent[0]])["class_type"]).Should().Be("VAEEncode");
    }

    [Fact]
    public void BuildFluxKontext_KSampler_PositiveFromGuidance_CfgAndDenoiseOne()
    {
        var wf  = KontextWf();
        var ks  = (Dictionary<string, object>)FindNode(wf, "KSampler")["inputs"];

        ks["cfg"].Should().Be(1.0);
        ((double)ks["denoise"]).Should().Be(1.0);

        var pos = (object[])ks["positive"];
        ((string)((Dictionary<string, object>)wf[(string)pos[0]])["class_type"]).Should().Be("FluxGuidance");
    }

    [Fact]
    public void BuildFluxKontext_FluxGuidance_UsesGivenGuidance()
    {
        var wf = KontextWf();
        var fg = (Dictionary<string, object>)FindNode(wf, "FluxGuidance")["inputs"];
        ((double)fg["guidance"]).Should().Be(2.5);
    }

    [Theory]
    [InlineData("flux1-dev-kontext_fp8_scaled.safetensors", true)]
    [InlineData("FLUX.1 Kontext dev",                       true)]
    [InlineData("flux1-dev.safetensors",                    false)]
    [InlineData("dreamshaper_xl.safetensors",               false)]
    public void IsKontextModel_CorrectlyIdentifiesKontext(string name, bool expected) =>
        ComfyWorkflowBuilder.IsKontextModel(name).Should().Be(expected);

    [Fact]
    public void KontextDefaults_Are20StepsGuidance2_5()
    {
        var (steps, guidance) = ComfyWorkflowBuilder.KontextDefaults;
        steps.Should().Be(20);
        guidance.Should().Be(2.5);
    }

    // ── BuildFluxPuLID (identita osoby bez tréninku) ──────────────────────────

    private static Dictionary<string, object> PulidWf() =>
        ComfyWorkflowBuilder.BuildFluxPuLID(
            "flux1-dev.safetensors", "clip_l.safetensors", "t5xxl.safetensors", "ae.safetensors",
            ComfyWorkflowBuilder.DefaultPulidFluxFile,
            "face.png", "cinematic portrait of a woman on a beach",
            832, 1216, 20, 3.5, seed: 7);

    [Fact]
    public void BuildFluxPuLID_HasPulidStackNodes()
    {
        var wf = PulidWf();
        var types = wf.Values.Cast<Dictionary<string, object>>().Select(n => (string)n["class_type"]).ToList();

        types.Should().Contain("PulidFluxModelLoader")
             .And.Contain("PulidFluxEvaClipLoader")
             .And.Contain("PulidFluxInsightFaceLoader")
             .And.Contain("ApplyPulidFlux");
    }

    [Fact]
    public void BuildFluxPuLID_ApplyPulid_WiredToModelAndFace()
    {
        var wf  = PulidWf();
        var ap  = (Dictionary<string, object>)FindNode(wf, "ApplyPulidFlux")["inputs"];

        // model ← UNETLoader, image ← LoadImage
        var model = (object[])ap["model"];
        var image = (object[])ap["image"];
        ((string)((Dictionary<string, object>)wf[(string)model[0]])["class_type"]).Should().Be("UNETLoader");
        ((string)((Dictionary<string, object>)wf[(string)image[0]])["class_type"]).Should().Be("LoadImage");
    }

    [Fact]
    public void BuildFluxPuLID_KSampler_UsesPulidModelOutput()
    {
        var wf = PulidWf();
        var ks = (Dictionary<string, object>)FindNode(wf, "KSampler")["inputs"];

        // KSampler musí brát model z ApplyPulidFlux (ne přímo z UNETLoaderu)
        var model = (object[])ks["model"];
        ((string)((Dictionary<string, object>)wf[(string)model[0]])["class_type"]).Should().Be("ApplyPulidFlux");
        ks["cfg"].Should().Be(1.0);
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

    // ── InjectLoras ───────────────────────────────────────────────────────────

    private static Dictionary<string, object> GetInputs(
        Dictionary<string, object> wf, string key)
        => (Dictionary<string, object>)((Dictionary<string, object>)wf[key])["inputs"];

    [Fact]
    public void InjectLoras_Empty_LeavesWorkflowUnchanged()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);
        var before = wf.Count;

        ComfyWorkflowBuilder.InjectLoras(wf, "4", ["3"], ["6", "7"], []);

        wf.Count.Should().Be(before);
    }

    [Fact]
    public void InjectLoras_SingleLora_AddsLoraLoaderNode()
    {
        var wf   = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);
        var lora = new LoraItem("style_lora.safetensors", 0.8, 0.8);

        ComfyWorkflowBuilder.InjectLoras(wf, "4", ["3"], ["6", "7"], [lora]);

        wf.Should().ContainKey("50");
        var loraNode = (Dictionary<string, object>)wf["50"];
        loraNode["class_type"].Should().Be("LoraLoader");
        GetInputs(wf, "50")["lora_name"].Should().Be("style_lora.safetensors");
    }

    [Fact]
    public void InjectLoras_SingleLora_KSamplerUsesLoraModelOutput()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);

        ComfyWorkflowBuilder.InjectLoras(wf, "4", ["3"], ["6", "7"],
            [new LoraItem("lora.safetensors")]);

        var ksamplerInputs = GetInputs(wf, "3");
        var modelRef       = (object[])ksamplerInputs["model"];
        modelRef[0].Should().Be("50");
        modelRef[1].Should().Be(0);
    }

    [Fact]
    public void InjectLoras_SingleLora_CLIPTextEncodeUsesLoraClipOutput()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);

        ComfyWorkflowBuilder.InjectLoras(wf, "4", ["3"], ["6", "7"],
            [new LoraItem("lora.safetensors")]);

        var clipRef = (object[])GetInputs(wf, "6")["clip"];
        clipRef[0].Should().Be("50");
        clipRef[1].Should().Be(1);
    }

    [Fact]
    public void InjectLoras_TwoLoras_ChainedCorrectly()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);

        var loras = new[]
        {
            new LoraItem("lora1.safetensors", 1.0, 1.0),
            new LoraItem("lora2.safetensors", 0.5, 0.5),
        };
        ComfyWorkflowBuilder.InjectLoras(wf, "4", ["3"], ["6", "7"], loras);

        // lora1 (ID 50) chains from checkpoint "4"
        var lora1In = GetInputs(wf, "50");
        ((object[])lora1In["model"])[0].Should().Be("4");

        // lora2 (ID 51) chains from lora1 (ID 50)
        var lora2In = GetInputs(wf, "51");
        ((object[])lora2In["model"])[0].Should().Be("50");

        // KSampler uses lora2 output
        ((object[])GetInputs(wf, "3")["model"])[0].Should().Be("51");
    }

    [Fact]
    public void InjectLoras_Strength_SetOnNode()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 512, 512, 20, 7.0, 1);

        ComfyWorkflowBuilder.InjectLoras(wf, "4", ["3"], ["6", "7"],
            [new LoraItem("lora.safetensors", StrengthModel: 0.75, StrengthClip: 0.6)]);

        var inputs = GetInputs(wf, "50");
        inputs["strength_model"].Should().Be(0.75);
        inputs["strength_clip"].Should().Be(0.6);
    }

    // ── AppendUpscale (hires fix + ESRGAN) ────────────────────────────────────

    [Fact]
    public void AppendUpscale_AddsHiresFix_AndRepointsSaveImage()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 1024, 1024, 20, 7.0, 1);

        ComfyWorkflowBuilder.AppendUpscale(wf, 1024, 1024, useUpscaleModel: false);

        var types = wf.Values.Cast<Dictionary<string, object>>().Select(n => (string)n["class_type"]).ToList();
        // Hires fix přidá LatentUpscale + 2. KSampler + 2. VAEDecode
        types.Should().Contain("LatentUpscale");
        types.Count(t => t == "KSampler").Should().Be(2);
        types.Count(t => t == "VAEDecode").Should().Be(2);
        // Bez ESRGAN se upscale model nepřidává
        types.Should().NotContain("UpscaleModelLoader");

        // SaveImage teď ukazuje na uzel, který NENÍ původní VAEDecode "8"
        var saveImg = FindNode(wf, "SaveImage");
        var imgRef  = (object[])((Dictionary<string, object>)saveImg["inputs"])["images"];
        ((string)imgRef[0]).Should().NotBe("8");
    }

    [Fact]
    public void AppendUpscale_HiresKSampler_ReusesModelRef_AndLowDenoise()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 1024, 1024, 20, 7.0, 1);

        // model ref původního KSampleru ("3") před upscale
        var origModelRef = (object[])GetInputs(wf, "3")["model"];

        ComfyWorkflowBuilder.AppendUpscale(wf, 1024, 1024,
            hiresDenoise: 0.35, useUpscaleModel: false);

        // 2. KSampler = ten, který NENÍ "3"
        var second = wf.Where(kv => kv.Value is Dictionary<string, object> n
                                    && (string)n["class_type"] == "KSampler"
                                    && kv.Key != "3")
                       .Select(kv => (Dictionary<string, object>)((Dictionary<string, object>)kv.Value)["inputs"])
                       .Single();

        ((double)second["denoise"]).Should().Be(0.35);
        ((object[])second["model"])[0].Should().Be(origModelRef[0]); // stejný model (vč. LoRA)
    }

    [Fact]
    public void AppendUpscale_WithUpscaleModel_AddsEsrganNodes()
    {
        var wf = ComfyWorkflowBuilder.BuildStandard(
            "model.safetensors", "cat", "", 1024, 1024, 20, 7.0, 1);

        ComfyWorkflowBuilder.AppendUpscale(wf, 1024, 1024,
            useUpscaleModel: true, upscaleModelName: "RealESRGAN_x4plus.pth", finalScale: 2.0);

        var types = wf.Values.Cast<Dictionary<string, object>>().Select(n => (string)n["class_type"]).ToList();
        types.Should().Contain("UpscaleModelLoader");
        types.Should().Contain("ImageUpscaleWithModel");

        var loader = (Dictionary<string, object>)FindNode(wf, "UpscaleModelLoader")["inputs"];
        loader["model_name"].Should().Be("RealESRGAN_x4plus.pth");
    }
}
