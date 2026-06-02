using AIStudio.Core.Models;
using AIStudio.Core.Services;
using FluentAssertions;
using Xunit;

namespace AIStudio.Tests;

/// <summary>
/// Testy routing logiky vytažené z ImageGeneratorViewModel do ImageWorkflowComposer.
/// Drží správný výběr base workflow × img2img × LoRA × upscale.
/// </summary>
public class ImageWorkflowComposerTests
{
    private static ImageWorkflowRequest Req(
        string model = "model.safetensors",
        bool isFlux = false, bool isGguf = false, bool fluxUnetOnly = false,
        string? uploadedRef = null,
        IReadOnlyList<LoraItem>? loras = null,
        bool enableUpscale = false, bool useEsrgan = false) =>
        new(
            Model: model, Prompt: "cat", NegativePrompt: "blurry",
            Width: 1024, Height: 1024, Steps: 20, Cfg: 7.0, Seed: 42, BatchSize: 1,
            Sampler: "euler", Scheduler: "normal",
            IsFlux: isFlux, IsGguf: isGguf, FluxUnetOnly: fluxUnetOnly,
            UploadedRefName: uploadedRef, ReferenceStrength: 0.6,
            Loras: loras ?? Array.Empty<LoraItem>(),
            EnableUpscale: enableUpscale, UseEsrganModel: useEsrgan,
            UpscaleModelName: "RealESRGAN_x4plus.pth");

    private static bool Has(Dictionary<string, object> wf, string classType) =>
        wf.Values.Cast<Dictionary<string, object>>()
          .Any(n => n.TryGetValue("class_type", out var t) && (string)t == classType);

    private static int Count(Dictionary<string, object> wf, string classType) =>
        wf.Values.Cast<Dictionary<string, object>>()
          .Count(n => n.TryGetValue("class_type", out var t) && (string)t == classType);

    [Fact]
    public void Txt2Img_SdSdxl_UsesCheckpointLoader()
    {
        var wf = ImageWorkflowComposer.Compose(Req());
        Has(wf, "CheckpointLoaderSimple").Should().BeTrue();
        Has(wf, "KSampler").Should().BeTrue();
        Has(wf, "FluxGuidance").Should().BeFalse();
    }

    [Fact]
    public void Txt2Img_FluxSafetensors_HasFluxGuidance()
    {
        var wf = ImageWorkflowComposer.Compose(Req(model: "flux1-dev.safetensors", isFlux: true));
        Has(wf, "FluxGuidance").Should().BeTrue();
        Has(wf, "CheckpointLoaderSimple").Should().BeTrue();
    }

    [Fact]
    public void Txt2Img_FluxGguf_UsesUnetLoaderGguf()
    {
        var wf = ImageWorkflowComposer.Compose(Req(model: "flux1-dev-Q4.gguf", isFlux: true, isGguf: true));
        Has(wf, "UnetLoaderGGUF").Should().BeTrue();
        Has(wf, "DualCLIPLoader").Should().BeTrue();
    }

    [Fact]
    public void Txt2Img_FluxUnetOnly_UsesUnetLoader()
    {
        var wf = ImageWorkflowComposer.Compose(Req(model: "unet/flux1-dev-fp8.safetensors", isFlux: true, fluxUnetOnly: true));
        Has(wf, "UNETLoader").Should().BeTrue();
        Has(wf, "DualCLIPLoader").Should().BeTrue();
        Has(wf, "UnetLoaderGGUF").Should().BeFalse();
    }

    [Fact]
    public void Img2Img_Sd_HasLoadImageAndVaeEncode()
    {
        var wf = ImageWorkflowComposer.Compose(Req(uploadedRef: "ref.png"));
        Has(wf, "LoadImage").Should().BeTrue();
        Has(wf, "VAEEncode").Should().BeTrue();
    }

    [Fact]
    public void Img2Img_FluxUnetOnly_InjectsReference()
    {
        var wf = ImageWorkflowComposer.Compose(
            Req(model: "unet/flux1-dev-fp8.safetensors", isFlux: true, fluxUnetOnly: true, uploadedRef: "ref.png"));
        Has(wf, "UNETLoader").Should().BeTrue();
        Has(wf, "LoadImage").Should().BeTrue();   // injektovaná reference
        Has(wf, "VAEEncode").Should().BeTrue();
    }

    [Fact]
    public void Img2Img_Gguf_FallsBackToTxt2Img_NoLoadImage()
    {
        // GGUF + reference → reference se ignoruje (img2img pro GGUF neumíme)
        var wf = ImageWorkflowComposer.Compose(
            Req(model: "flux1-dev-Q4.gguf", isFlux: true, isGguf: true, uploadedRef: "ref.png"));
        Has(wf, "UnetLoaderGGUF").Should().BeTrue();
        Has(wf, "LoadImage").Should().BeFalse();
    }

    [Fact]
    public void Loras_Injected_AddLoraLoaderNodes()
    {
        var wf = ImageWorkflowComposer.Compose(Req(loras: new[]
        {
            new LoraItem("a.safetensors", StrengthModel: 0.8, StrengthClip: 0.8),
            new LoraItem("b.safetensors", StrengthModel: 0.5, StrengthClip: 0.5),
        }));
        Count(wf, "LoraLoader").Should().Be(2);
    }

    [Fact]
    public void Loras_None_NoLoraLoader()
    {
        var wf = ImageWorkflowComposer.Compose(Req());
        Has(wf, "LoraLoader").Should().BeFalse();
    }

    [Fact]
    public void Upscale_Enabled_AppendsHiresAndEsrgan()
    {
        var wf = ImageWorkflowComposer.Compose(Req(enableUpscale: true, useEsrgan: true));
        Has(wf, "LatentUpscale").Should().BeTrue();        // hires fix
        Count(wf, "KSampler").Should().Be(2);              // 2. průchod
        Has(wf, "UpscaleModelLoader").Should().BeTrue();   // ESRGAN
    }

    [Fact]
    public void Upscale_Disabled_NoUpscaleNodes()
    {
        var wf = ImageWorkflowComposer.Compose(Req());
        Has(wf, "LatentUpscale").Should().BeFalse();
        Count(wf, "KSampler").Should().Be(1);
    }
}
