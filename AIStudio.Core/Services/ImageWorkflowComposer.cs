using AIStudio.Core.Models;

namespace AIStudio.Core.Services;

/// <summary>
/// Vstup pro <see cref="ImageWorkflowComposer"/> — vše, co rozhoduje o podobě
/// ComfyUI workflow. Čistá data, žádné I/O: reference je už nahraná (název v
/// ComfyUI), ESRGAN model už ověřený. Díky tomu je composer plně testovatelný.
/// </summary>
public sealed record ImageWorkflowRequest(
    string Model,
    string Prompt,
    string NegativePrompt,
    int    Width,
    int    Height,
    int    Steps,
    double Cfg,
    long   Seed,
    int    BatchSize,
    string Sampler,
    string Scheduler,
    bool   IsFlux,
    bool   IsGguf,
    bool   FluxUnetOnly,
    string? UploadedRefName,
    double ReferenceStrength,
    IReadOnlyList<LoraItem> Loras,
    bool   EnableUpscale,
    bool   UseEsrganModel,
    string UpscaleModelName);

/// <summary>
/// Sestaví kompletní ComfyUI workflow z <see cref="ImageWorkflowRequest"/>:
/// vybere správný base workflow (txt2img / img2img × SD/SDXL/FLUX/GGUF/UNET),
/// injektuje LoRA a volitelně připojí upscale tail.
///
/// <para>Tahle logika dřív žila uvnitř <c>ImageGeneratorViewModel.GenerateAsync</c>
/// (~150 ř. větvení). Vytažením do čisté statické funkce zmizela byznys logika
/// z prezentační vrstvy a routing je nově pokrytý unit testy
/// (<c>ImageWorkflowComposerTests</c>).</para>
/// </summary>
public static class ImageWorkflowComposer
{
    public static Dictionary<string, object> Compose(ImageWorkflowRequest r)
    {
        var workflow = BuildBase(r);
        InjectLoras(workflow, r);

        // Upscale tail (hires fix + ESRGAN) — AŽ po LoRA, protože AppendUpscale
        // přebírá model/cond z existujícího KSampleru (2. průchod respektuje LoRA).
        if (r.EnableUpscale)
            ComfyWorkflowBuilder.AppendUpscale(
                workflow, r.Width, r.Height,
                hiresScale: 1.5, hiresDenoise: 0.35,
                useUpscaleModel: r.UseEsrganModel,
                upscaleModelName: r.UpscaleModelName,
                finalScale: 2.0);

        return workflow;
    }

    /// <summary>
    /// Vybere base workflow. Pro GGUF + referenci se reference ignoruje (img2img
    /// pro GGUF zatím nepodporujeme) — volající o tom uživatele informuje.
    /// </summary>
    private static Dictionary<string, object> BuildBase(ImageWorkflowRequest r)
    {
        var clipL = ComfyWorkflowBuilder.DefaultFluxClipL;
        var t5    = ComfyWorkflowBuilder.DefaultFluxT5;
        var vae   = ComfyWorkflowBuilder.DefaultFluxVae;
        var denoise = ComfyWorkflowBuilder.CreativityToDenoise(r.ReferenceStrength);

        if (r.UploadedRefName is not null)
        {
            // ── IMG2IMG ──
            if (r.IsFlux && r.FluxUnetOnly)
            {
                // UNET-only FLUX nemá vlastní VAE/CLIP → BuildFluxUnet + generický
                // LatentBlend img2img injektor.
                var wf = ComfyWorkflowBuilder.BuildFluxUnet(
                    r.Model, clipL, t5, vae,
                    r.Prompt, r.Width, r.Height, r.Steps, r.Cfg, r.Seed, r.BatchSize,
                    r.Sampler, r.Scheduler);
                ComfyWorkflowBuilder.InjectReferenceImages(
                    wf,
                    ComfyWorkflowBuilder.FluxGgufEmptyLatentKey,
                    ComfyWorkflowBuilder.FluxGgufKSamplerKey,
                    ComfyWorkflowBuilder.FluxGgufVaeRef,
                    new[] { r.UploadedRefName },
                    r.Width, r.Height, r.ReferenceStrength, r.BatchSize);
                return wf;
            }
            if (r.IsFlux && !r.IsGguf)
                return ComfyWorkflowBuilder.BuildFluxImg2Img(
                    r.Model, r.UploadedRefName,
                    r.Prompt, r.Width, r.Height, r.Steps, r.Cfg, r.Seed, denoise, r.BatchSize,
                    r.Sampler, r.Scheduler);
            if (r.IsGguf)
                // GGUF img2img zatím neumíme → txt2img (reference ignorována).
                return ComfyWorkflowBuilder.BuildFluxGguf(
                    r.Model, clipL, t5, vae,
                    r.Prompt, r.Width, r.Height, r.Steps, r.Cfg, r.Seed, r.BatchSize,
                    r.Sampler, r.Scheduler);
            return ComfyWorkflowBuilder.BuildStandardImg2Img(
                r.Model, r.UploadedRefName,
                r.Prompt, r.NegativePrompt, r.Width, r.Height, r.Steps, r.Cfg, r.Seed, denoise, r.BatchSize,
                r.Sampler, r.Scheduler);
        }

        // ── TXT2IMG ──
        if (r.IsFlux && r.IsGguf)
            return ComfyWorkflowBuilder.BuildFluxGguf(
                r.Model, clipL, t5, vae,
                r.Prompt, r.Width, r.Height, r.Steps, r.Cfg, r.Seed, r.BatchSize,
                r.Sampler, r.Scheduler);
        if (r.IsFlux && r.FluxUnetOnly)
            return ComfyWorkflowBuilder.BuildFluxUnet(
                r.Model, clipL, t5, vae,
                r.Prompt, r.Width, r.Height, r.Steps, r.Cfg, r.Seed, r.BatchSize,
                r.Sampler, r.Scheduler);
        if (r.IsFlux)
            return ComfyWorkflowBuilder.BuildFlux(
                r.Model, r.Prompt, r.Width, r.Height, r.Steps, r.Cfg, r.Seed, r.BatchSize,
                r.Sampler, r.Scheduler);
        return ComfyWorkflowBuilder.BuildStandard(
            r.Model, r.Prompt, r.NegativePrompt, r.Width, r.Height, r.Steps, r.Cfg, r.Seed, r.BatchSize,
            r.Sampler, r.Scheduler);
    }

    /// <summary>
    /// Injektuje LoRA řetěz dle topologie zvoleného workflow. GGUF i UNET-only
    /// FLUX mají oddělené loadery (model + clip ze dvou uzlů); ostatní mají
    /// checkpoint s model+clip v jednom uzlu, s jinými klíči pro txt2img vs img2img.
    /// </summary>
    private static void InjectLoras(Dictionary<string, object> workflow, ImageWorkflowRequest r)
    {
        if (r.Loras.Count == 0) return;

        if (r.IsGguf || r.FluxUnetOnly)
        {
            ComfyWorkflowBuilder.InjectLoras(
                workflow,
                initialModelRef:   ComfyWorkflowBuilder.GgufModelRef,
                initialClipRef:    ComfyWorkflowBuilder.GgufClipRef,
                modelConsumerKeys: new[] { "8" },
                clipConsumerKeys:  new[] { "5", "6" },
                r.Loras);
        }
        else if (r.UploadedRefName is not null)
        {
            // SD/SDXL img2img: KSampler "7"; FLUX img2img: KSampler "8"; CLIP "5","6".
            var imgModelNode = r.IsFlux ? "8" : "7";
            ComfyWorkflowBuilder.InjectLoras(
                workflow, checkpointKey: "1",
                modelConsumerKeys: new[] { imgModelNode },
                clipConsumerKeys:  new[] { "5", "6" },
                r.Loras);
        }
        else
        {
            // Txt2img: FLUX checkpoint "1" (KSampler "6", CLIP "3","4"),
            //          SD/SDXL checkpoint "4" (KSampler "3", CLIP "6","7").
            var (ckptKey, modelNodes, clipNodes) = r.IsFlux
                ? ("1", new[] { "6" }, new[] { "3", "4" })
                : ("4", new[] { "3" }, new[] { "6", "7" });
            ComfyWorkflowBuilder.InjectLoras(workflow, ckptKey, modelNodes, clipNodes, r.Loras);
        }
    }
}
