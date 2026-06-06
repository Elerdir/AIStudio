using System.Runtime.InteropServices;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// P/Invoke binding na <c>stable-diffusion.cpp</c> (ggml/GGUF) — vestavěná image inference
/// bez ComfyUI. Mirror přístupu LlamaSharp/llama.cpp: nativní lib se přibalí per backend
/// (CPU/CUDA/Vulkan/Metal) a runtime se dispatchuje.
///
/// <para><b>⚠️ NEAKTUÁLNÍ SIGNATURY — NEPOUŽÍVAT bez přepsání (viz docs/native-generator-design.md §6).</b>
/// Ověřeno proti reálné hlavičce releasu <c>master-672-1f9ee88</c>: poziční
/// <c>new_sd_ctx(...)</c>/<c>txt2img(...)</c> níže <b>v aktuálním sd.cpp UŽ NEEXISTUJÍ</b>.
/// Současné API je struct-based: <c>new_sd_ctx(const sd_ctx_params_t*)</c> +
/// <c>generate_image(ctx, const sd_img_gen_params_t*)</c> (vnořené struktury). Tyhle deklarace
/// by spadly na <c>EntryPointNotFound</c>/ABI mismatch. Zvažuje se pivot na bundled
/// <c>sd-cli.exe</c> (shell-out, stabilní args) místo křehkého struct marshalingu.</para>
///
/// <para>Dokud lib není přibalená, <see cref="IsLibraryPresent"/> vrací false a
/// <see cref="NativeImageGenerator"/> hlásí „nedostupné" + fallback na ComfyUI; všechna volání
/// jsou obalená try/catch, takže chybějící/nesedící lib nezpůsobí pád, jen graceful chybu.</para>
/// </summary>
internal static class StableDiffusionInterop
{
    /// <summary>Základní název nativní knihovny (bez prefixu/přípony — <c>NativeLibrary</c> doplní per OS).</summary>
    public const string Lib = "stable-diffusion";

    /// <summary>
    /// Zjistí, zda je nativní knihovna k dispozici (bez pádu, když chybí). Pohání
    /// <c>NativeGeneratorStatus.IsAvailable</c>. Dnes (bez přibalené liby) vrací false všude.
    /// </summary>
    public static bool IsLibraryPresent()
    {
        try
        {
            if (NativeLibrary.TryLoad(Lib, typeof(StableDiffusionInterop).Assembly, searchPath: null, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch { /* TryLoad nehází, ale pro jistotu */ }
        return false;
    }

    /// <summary>Výstupní obrázek z sd.cpp — syrový pixelový buffer (RGB/RGBA).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SdImage
    {
        public uint   Width;
        public uint   Height;
        public uint   Channel;   // typicky 3 (RGB)
        public IntPtr Data;      // uint8_t* délky Width*Height*Channel
    }

    /// <summary>sd.cpp <c>sample_method_t</c> — pořadí MUSÍ odpovídat headeru (ověřit ve Fázi 2).</summary>
    public enum SampleMethod
    {
        EulerA = 0, Euler, Heun, Dpm2, Dpmpp2sA, Dpmpp2m, Dpmpp2mv2, Ipndm, IpndmV, Lcm, DdimTrailing, Tcd,
    }

    /// <summary>Mapuje sd.cpp název samodleru (<see cref="AIStudio.Core.Services.NativeSamplerMap"/>) na enum.</summary>
    public static SampleMethod SampleMethodFromName(string sdCppName) => sdCppName switch
    {
        "euler_a"       => SampleMethod.EulerA,
        "euler"         => SampleMethod.Euler,
        "heun"          => SampleMethod.Heun,
        "dpm2"          => SampleMethod.Dpm2,
        "dpm++2s_a"     => SampleMethod.Dpmpp2sA,
        "dpm++2m"       => SampleMethod.Dpmpp2m,
        "dpm++2mv2"     => SampleMethod.Dpmpp2mv2,
        "ipndm"         => SampleMethod.Ipndm,
        "ipndm_v"       => SampleMethod.IpndmV,
        "lcm"           => SampleMethod.Lcm,
        "ddim_trailing" => SampleMethod.DdimTrailing,
        "tcd"           => SampleMethod.Tcd,
        _               => SampleMethod.Euler,
    };

    // ── Nativní entry pointy (pinnuté, ověřit ve Fázi 2) ───────────────────────
    //
    // sd_ctx_t* new_sd_ctx(model_path, clip_l, clip_g, t5xxl, diffusion_model, vae, taesd,
    //   control_net, lora_model_dir, embed_dir, stacked_id_embed_dir, vae_decode_only,
    //   vae_tiling, free_params_immediately, n_threads, wtype, rng_type, schedule,
    //   keep_clip_on_cpu, keep_control_net_cpu, keep_vae_on_cpu, diffusion_flash_attn);

    [DllImport(Lib, EntryPoint = "new_sd_ctx", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NewSdCtx(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string clipLPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string clipGPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string t5xxlPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string diffusionModelPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string vaePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string taesdPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string controlNetPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string loraModelDir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string embedDir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stackedIdEmbedDir,
        [MarshalAs(UnmanagedType.I1)] bool vaeDecodeOnly,
        [MarshalAs(UnmanagedType.I1)] bool vaeTiling,
        [MarshalAs(UnmanagedType.I1)] bool freeParamsImmediately,
        int nThreads,
        int wtype,           // sd_type_t (-1 = neměnit / dle modelu)
        int rngType,         // rng_type_t (0 = std default)
        int schedule,        // schedule_t (0 = default)
        [MarshalAs(UnmanagedType.I1)] bool keepClipOnCpu,
        [MarshalAs(UnmanagedType.I1)] bool keepControlNetCpu,
        [MarshalAs(UnmanagedType.I1)] bool keepVaeOnCpu,
        [MarshalAs(UnmanagedType.I1)] bool diffusionFlashAttn);

    // sd_image_t* txt2img(sd_ctx, prompt, negative, clip_skip, cfg_scale, guidance, eta,
    //   width, height, sample_method, sample_steps, seed, batch_count, control_cond,
    //   control_strength, style_strength, normalize_input, input_id_images_path,
    //   skip_layers, skip_layers_count, slg_scale, skip_layer_start, skip_layer_end);

    [DllImport(Lib, EntryPoint = "txt2img", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Txt2Img(
        IntPtr sdCtx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string negativePrompt,
        int clipSkip,
        float cfgScale,
        float guidance,
        float eta,
        int width,
        int height,
        int sampleMethod,
        int sampleSteps,
        long seed,
        int batchCount,
        IntPtr controlCond,
        float controlStrength,
        float styleStrength,
        [MarshalAs(UnmanagedType.I1)] bool normalizeInput,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string inputIdImagesPath,
        IntPtr skipLayers,
        nuint skipLayersCount,
        float slgScale,
        float skipLayerStart,
        float skipLayerEnd);

    [DllImport(Lib, EntryPoint = "free_sd_ctx", CallingConvention = CallingConvention.Cdecl)]
    public static extern void FreeSdCtx(IntPtr sdCtx);
}
