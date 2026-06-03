using AIStudio.Core.Models;

namespace AIStudio.Core.Services;

/// <summary>
/// Sestavuje workflow JSON pro ComfyUI API.
/// Podporuje standardní SD/SDXL checkpoint a FLUX checkpoint.
/// </summary>
public static class ComfyWorkflowBuilder
{
    // ── Výchozí sampler / scheduler ───────────────────────────────────────────

    /// <summary>Výchozí sampler pro SD/SDXL workflow (DPM++ 2M — nejlepší kvalita).</summary>
    public const string DefaultSamplerSd   = "dpmpp_2m";
    /// <summary>Výchozí scheduler pro SD/SDXL workflow (Karras — ostřejší výsledky).</summary>
    public const string DefaultSchedulerSd = "karras";
    /// <summary>Výchozí sampler pro FLUX workflow.</summary>
    public const string DefaultSamplerFlux   = "euler";
    /// <summary>Výchozí scheduler pro FLUX workflow.</summary>
    public const string DefaultSchedulerFlux = "simple";

    // ── SD / SDXL ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Klasický 9-uzlový workflow vhodný pro SD 1.5, SDXL, Pony, Juggernaut, DreamShaper apod.
    /// </summary>
    public static Dictionary<string, object> BuildStandard(
        string checkpoint,
        string prompt,
        string negativePrompt,
        int    width,
        int    height,
        int    steps,
        double cfg,
        long   seed,
        int    batchSize = 1,
        string sampler   = DefaultSamplerSd,
        string scheduler = DefaultSchedulerSd)
    {
        return new Dictionary<string, object>
        {
            ["4"] = Node("CheckpointLoaderSimple", new()
            {
                ["ckpt_name"] = checkpoint,
            }),
            ["5"] = Node("EmptyLatentImage", new()
            {
                ["width"]      = width,
                ["height"]     = height,
                ["batch_size"] = batchSize,
            }),
            ["6"] = Node("CLIPTextEncode", new()
            {
                ["text"] = prompt,
                ["clip"] = Ref("4", 1),
            }),
            ["7"] = Node("CLIPTextEncode", new()
            {
                ["text"] = negativePrompt,
                ["clip"] = Ref("4", 1),
            }),
            ["3"] = Node("KSampler", new()
            {
                ["seed"]          = seed,
                ["steps"]         = steps,
                ["cfg"]           = cfg,
                ["sampler_name"]  = sampler,
                ["scheduler"]     = scheduler,
                ["denoise"]       = 1.0,
                ["model"]         = Ref("4", 0),
                ["positive"]      = Ref("6", 0),
                ["negative"]      = Ref("7", 0),
                ["latent_image"]  = Ref("5", 0),
            }),
            ["8"] = Node("VAEDecode", new()
            {
                ["samples"] = Ref("3", 0),
                ["vae"]     = Ref("4", 2),
            }),
            ["9"] = Node("SaveImage", new()
            {
                ["filename_prefix"] = "AIStudio",
                ["images"]          = Ref("8", 0),
            }),
        };
    }

    // ── FLUX ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Workflow pro FLUX.1 Schnell / Dev checkpointy (unet + t5 + clip_l + ae).
    /// CFG se ignoruje (FLUX Schnell: guidance = 0, Dev: guidance ~ 3.5).
    /// </summary>
    public static Dictionary<string, object> BuildFlux(
        string checkpoint,
        string prompt,
        int    width,
        int    height,
        int    steps,
        double guidance,
        long   seed,
        int    batchSize = 1,
        string sampler   = DefaultSamplerFlux,
        string scheduler = DefaultSchedulerFlux)
    {
        return new Dictionary<string, object>
        {
            // Pro FLUX "all-in-one" checkpointy stačí CheckpointLoaderSimple
            ["1"] = Node("CheckpointLoaderSimple", new()
            {
                ["ckpt_name"] = checkpoint,
            }),
            ["2"] = Node("EmptyLatentImage", new()
            {
                ["width"]      = width,
                ["height"]     = height,
                ["batch_size"] = batchSize,
            }),
            ["3"] = Node("CLIPTextEncode", new()
            {
                ["text"] = prompt,
                ["clip"] = Ref("1", 1),
            }),
            // FLUX nepotřebuje negative prompt — použijeme prázdný
            ["4"] = Node("CLIPTextEncode", new()
            {
                ["text"] = "",
                ["clip"] = Ref("1", 1),
            }),
            ["5"] = Node("FluxGuidance", new()
            {
                ["conditioning"] = Ref("3", 0),
                ["guidance"]     = guidance,
            }),
            ["6"] = Node("KSampler", new()
            {
                ["seed"]         = seed,
                ["steps"]        = steps,
                ["cfg"]          = 1.0,
                ["sampler_name"] = sampler,
                ["scheduler"]    = scheduler,
                ["denoise"]      = 1.0,
                ["model"]        = Ref("1", 0),
                ["positive"]     = Ref("5", 0),
                ["negative"]     = Ref("4", 0),
                ["latent_image"] = Ref("2", 0),
            }),
            ["7"] = Node("VAEDecode", new()
            {
                ["samples"] = Ref("6", 0),
                ["vae"]     = Ref("1", 2),
            }),
            ["8"] = Node("SaveImage", new()
            {
                ["filename_prefix"] = "AIStudio",
                ["images"]          = Ref("7", 0),
            }),
        };
    }

    // ── FLUX GGUF / split-files ───────────────────────────────────────────────

    /// <summary>
    /// Standardní názvy závislostí pro FLUX (CLIP-L, T5 XXL, VAE).
    /// Workflow počítá s tím, že tyto soubory leží v Models adresáři
    /// a ComfyUI je vidí přes extra_model_paths.yaml.
    /// </summary>
    public const string DefaultFluxClipL  = "clip_l.safetensors";
    public const string DefaultFluxT5     = "t5xxl_fp8_e4m3fn.safetensors";
    public const string DefaultFluxVae    = "ae.safetensors";

    /// <summary>
    /// Workflow pro FLUX GGUF kvantizace. Vyžaduje custom node ComfyUI-GGUF
    /// (UnetLoaderGGUF) a samostatné soubory CLIP-L + T5 + VAE v Models složce.
    /// </summary>
    public static Dictionary<string, object> BuildFluxGguf(
        string unetGgufFile,
        string clipLFile,
        string t5File,
        string vaeFile,
        string prompt,
        int    width,
        int    height,
        int    steps,
        double guidance,
        long   seed,
        int    batchSize = 1,
        string sampler   = DefaultSamplerFlux,
        string scheduler = DefaultSchedulerFlux)
    {
        return new Dictionary<string, object>
        {
            ["1"] = Node("UnetLoaderGGUF", new()
            {
                ["unet_name"] = unetGgufFile,
            }),
            ["2"] = Node("DualCLIPLoader", new()
            {
                ["clip_name1"] = clipLFile,
                ["clip_name2"] = t5File,
                ["type"]       = "flux",
            }),
            ["3"] = Node("VAELoader", new()
            {
                ["vae_name"] = vaeFile,
            }),
            ["4"] = Node("EmptyLatentImage", new()
            {
                ["width"]      = width,
                ["height"]     = height,
                ["batch_size"] = batchSize,
            }),
            ["5"] = Node("CLIPTextEncode", new()
            {
                ["text"] = prompt,
                ["clip"] = Ref("2", 0),
            }),
            // FLUX nepoužívá negative prompt, ale KSampler ho očekává — dáme prázdný
            ["6"] = Node("CLIPTextEncode", new()
            {
                ["text"] = "",
                ["clip"] = Ref("2", 0),
            }),
            ["7"] = Node("FluxGuidance", new()
            {
                ["conditioning"] = Ref("5", 0),
                ["guidance"]     = guidance,
            }),
            ["8"] = Node("KSampler", new()
            {
                ["seed"]         = seed,
                ["steps"]        = steps,
                ["cfg"]          = 1.0,
                ["sampler_name"] = sampler,
                ["scheduler"]    = scheduler,
                ["denoise"]      = 1.0,
                ["model"]        = Ref("1", 0),
                ["positive"]     = Ref("7", 0),
                ["negative"]     = Ref("6", 0),
                ["latent_image"] = Ref("4", 0),
            }),
            ["9"] = Node("VAEDecode", new()
            {
                ["samples"] = Ref("8", 0),
                ["vae"]     = Ref("3", 0),
            }),
            ["10"] = Node("SaveImage", new()
            {
                ["filename_prefix"] = "AIStudio",
                ["images"]          = Ref("9", 0),
            }),
        };
    }

    /// <summary>
    /// Workflow pro FLUX UNET-only <c>.safetensors</c> (diffusion_models / unet/),
    /// např. <c>flux1-dev-fp8.safetensors</c> od Kijai. Tyto soubory obsahují JEN
    /// transformer (UNET) — NE CLIP ani VAE — takže <c>CheckpointLoaderSimple</c>
    /// selže na „clip input is invalid: None". Místo toho zavádíme model přes
    /// <c>UNETLoader</c> a CLIP + VAE z oddělených souborů (stejně jako GGUF).
    ///
    /// <para>Topologie uzlů je IDENTICKÁ s <see cref="BuildFluxGguf"/> (loader=„1",
    /// DualCLIP=„2", VAE=„3", EmptyLatent=„4", CLIP+=„5", CLIP-=„6", FluxGuidance=„7",
    /// KSampler=„8", VAEDecode=„9", SaveImage=„10"), takže LoRA injection i reference
    /// injection používají stejné <c>FluxGguf*</c> klíče.</para>
    /// </summary>
    public static Dictionary<string, object> BuildFluxUnet(
        string unetFile,
        string clipLFile,
        string t5File,
        string vaeFile,
        string prompt,
        int    width,
        int    height,
        int    steps,
        double guidance,
        long   seed,
        int    batchSize = 1,
        string sampler   = DefaultSamplerFlux,
        string scheduler = DefaultSchedulerFlux)
    {
        var wf = BuildFluxGguf(unetFile, clipLFile, t5File, vaeFile,
                               prompt, width, height, steps, guidance, seed,
                               batchSize, sampler, scheduler);

        // Vyměníme UnetLoaderGGUF (uzel „1") za UNETLoader pro .safetensors UNET.
        // weight_dtype="default" → ComfyUI auto-detekuje fp8/bf16 z hlavičky souboru
        // (log to potvrdil: „model weight dtype torch.float8_e4m3fn").
        wf["1"] = Node("UNETLoader", new()
        {
            ["unet_name"]    = unetFile,
            ["weight_dtype"] = "default",
        });
        return wf;
    }

    /// <summary>
    /// Samostatný upscale HOTOVÉHO obrázku (ESRGAN model). LoadImage → UpscaleModelLoader
    /// → ImageUpscaleWithModel (typicky 4×) → ImageScaleBy zpět na cílový násobek
    /// (<paramref name="netScale"/>, default 2× = 4× model / 2). Bez diffusion modelu —
    /// rychlé, nepotřebuje checkpoint ani prompt. Použito pro „upscale ikonu" v chatu.
    /// </summary>
    public static Dictionary<string, object> BuildImageUpscale(
        string uploadedImageName,
        string upscaleModelName,
        double netScale = 2.0,
        double modelScale = 4.0)
    {
        return new Dictionary<string, object>
        {
            ["1"] = Node("LoadImage", new()
            {
                ["image"]  = uploadedImageName,
                ["upload"] = "image",
            }),
            ["2"] = Node("UpscaleModelLoader", new() { ["model_name"] = upscaleModelName }),
            ["3"] = Node("ImageUpscaleWithModel", new()
            {
                ["upscale_model"] = Ref("2", 0),
                ["image"]         = Ref("1", 0),
            }),
            // ESRGAN model dává modelScale× (4×). Doškálujeme na cílový netScale×
            // (2×) — vyhneme se obřím 4096px souborům, zachováme ostrost.
            ["4"] = Node("ImageScaleBy", new()
            {
                ["image"]          = Ref("3", 0),
                ["upscale_method"] = "lanczos",
                ["scale_by"]       = netScale / modelScale,
            }),
            ["5"] = Node("SaveImage", new()
            {
                ["filename_prefix"] = "AIStudio_upscale",
                ["images"]          = Ref("4", 0),
            }),
        };
    }

    // ── Reference images (img2img / multi-reference blend) ───────────────────

    /// <summary>
    /// Do existujícího workflow injektuje řetězec LoadImage → ImageScale → VAEEncode
    /// pro každý referenční obrázek a slije jejich latenty pomocí <c>LatentBlend</c>
    /// (rovnoměrně). Výsledný latent se použije jako <c>latent_image</c> v KSampleru
    /// a denoise se nastaví na <c>1 - strength</c>.
    ///
    /// Funguje pro libovolný workflow, kde:
    ///   • existuje EmptyLatentImage uzel (bude odebrán — KSampler na něj nemá záviset),
    ///   • máme referenci na VAE (<paramref name="vaeRef"/>) a KSampler uzel.
    ///
    /// Bez vlastních custom nodů (LatentBlend i ImageScale jsou v jádru ComfyUI),
    /// takže tohle běhá na čisté instalaci. Pro pokročilé use-case (IP-Adapter,
    /// ControlNet, Redux) přijde samostatná cesta později.
    /// </summary>
    /// <param name="workflow">Workflow, ve kterém se mají reference zapojit.</param>
    /// <param name="emptyLatentKey">ID uzlu EmptyLatentImage — bude odebrán.</param>
    /// <param name="ksamplerKey">ID uzlu KSampler — přepíše se latent_image + denoise.</param>
    /// <param name="vaeRef">Reference (Ref(...)) na VAE výstup loaderu.</param>
    /// <param name="referenceImageNames">Jména souborů v ComfyUI input/ (po uploadu).</param>
    /// <param name="width">Cílová šířka — všechny reference se přeškálují.</param>
    /// <param name="height">Cílová výška.</param>
    /// <param name="strength">0–1: kolik z reference zachovat (0 = ignoruj, 1 = identicky).</param>
    /// <param name="batchSize">Pokud > 1, přidá RepeatLatentBatch (varianty z téhož startu).</param>
    public static void InjectReferenceImages(
        Dictionary<string, object> workflow,
        string                     emptyLatentKey,
        string                     ksamplerKey,
        object                     vaeRef,
        IReadOnlyList<string>      referenceImageNames,
        int                        width,
        int                        height,
        double                     strength,
        int                        batchSize = 1)
    {
        if (referenceImageNames is null || referenceImageNames.Count == 0) return;

        // EmptyLatentImage už nepotřebujeme — orphan uzly ComfyUI sice ignoruje,
        // ale čisté workflow nepotřebuje vizuální mrtvolky.
        if (workflow.ContainsKey(emptyLatentKey))
            workflow.Remove(emptyLatentKey);

        // Vyhradíme „vysoké" node ID, abychom se netrefili do existujících
        // 1-10 čísel, která používají všechny tři Build* funkce.
        int nextId = 100;

        // 1) LoadImage + ImageScale + VAEEncode pro každý reference image
        var latentNodeIds = new List<string>(referenceImageNames.Count);

        foreach (var imgName in referenceImageNames)
        {
            var loadId  = (nextId++).ToString();
            var scaleId = (nextId++).ToString();
            var encId   = (nextId++).ToString();

            workflow[loadId]  = Node("LoadImage",  new() { ["image"] = imgName });

            // ImageScale (lanczos, center crop) sjednotí všechny reference do
            // cílového rozlišení — bez toho by VAEEncode pro různě velké obrázky
            // produkoval různě velké latenty a LatentBlend by selhal.
            workflow[scaleId] = Node("ImageScale", new()
            {
                ["image"]          = Ref(loadId, 0),
                ["upscale_method"] = "lanczos",
                ["width"]          = width,
                ["height"]         = height,
                ["crop"]           = "center",
            });

            workflow[encId] = Node("VAEEncode", new()
            {
                ["pixels"] = Ref(scaleId, 0),
                ["vae"]    = vaeRef,
            });

            latentNodeIds.Add(encId);
        }

        // 2) Slijeme latenty rovnoměrným průměrem přes řetězec LatentBlend.
        //    Pro N obrázků: postupně blendujeme s ratio 1/(i+1), aby každý měl
        //    ve výsledku stejnou váhu (running mean).
        var blendedLatentId = latentNodeIds[0];
        for (int i = 1; i < latentNodeIds.Count; i++)
        {
            var blendId = (nextId++).ToString();
            workflow[blendId] = Node("LatentBlend", new()
            {
                ["samples1"]     = Ref(blendedLatentId, 0),
                ["samples2"]     = Ref(latentNodeIds[i], 0),
                ["blend_factor"] = 1.0 / (i + 1),
            });
            blendedLatentId = blendId;
        }

        // 3) Pokud uživatel chce víc variant (batch), zopakujeme stejný startovací
        //    latent N-krát. KSampler pak pro každou batch position použije seed+offset,
        //    takže výsledné varianty se budou lišit.
        if (batchSize > 1)
        {
            var repeatId = (nextId++).ToString();
            workflow[repeatId] = Node("RepeatLatentBatch", new()
            {
                ["samples"] = Ref(blendedLatentId, 0),
                ["amount"]  = batchSize,
            });
            blendedLatentId = repeatId;
        }

        // 4) Přepíšeme KSampler — latent_image míří na blended start, denoise = 1-strength.
        if (workflow.TryGetValue(ksamplerKey, out var ksObj) &&
            ksObj is Dictionary<string, object> ksDict &&
            ksDict.TryGetValue("inputs", out var inObj) &&
            inObj is Dictionary<string, object> ksInputs)
        {
            ksInputs["latent_image"] = Ref(blendedLatentId, 0);
            ksInputs["denoise"]      = Math.Clamp(1.0 - strength, 0.0, 1.0);
        }
    }

    /// <summary>VAE reference pro Standard SD/SDXL workflow (CheckpointLoaderSimple, výstup 2).</summary>
    public static object StandardVaeRef => Ref("4", 2);

    /// <summary>VAE reference pro FLUX safetensors workflow.</summary>
    public static object FluxVaeRef     => Ref("1", 2);

    /// <summary>VAE reference pro FLUX GGUF workflow (samostatný VAELoader).</summary>
    public static object FluxGgufVaeRef => Ref("3", 0);

    /// <summary>ID EmptyLatentImage uzlu pro Standard workflow.</summary>
    public const string StandardEmptyLatentKey = "5";

    /// <summary>ID KSampler uzlu pro Standard workflow.</summary>
    public const string StandardKSamplerKey    = "3";

    /// <summary>ID EmptyLatentImage uzlu pro FLUX safetensors workflow.</summary>
    public const string FluxEmptyLatentKey     = "2";

    /// <summary>ID KSampler uzlu pro FLUX safetensors workflow.</summary>
    public const string FluxKSamplerKey        = "6";

    /// <summary>ID EmptyLatentImage uzlu pro FLUX GGUF workflow.</summary>
    public const string FluxGgufEmptyLatentKey = "4";

    /// <summary>ID KSampler uzlu pro FLUX GGUF workflow.</summary>
    public const string FluxGgufKSamplerKey    = "8";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object> Node(string classType,
                                                    Dictionary<string, object> inputs)
        => new() { ["class_type"] = classType, ["inputs"] = inputs };

    /// <summary>Odkaz na výstup jiného uzlu: ["nodeId", outputIndex].</summary>
    private static object[] Ref(string nodeId, int outputIndex)
        => new object[] { nodeId, outputIndex };

    // ── Wan 2.1 video ─────────────────────────────────────────────────────────
    //
    // Nativní ComfyUI podpora (bez křehkých custom nodů). Topologie odpovídá
    // oficiálním example workflow z Comfy-Org/Wan_2.1_ComfyUI_repackaged:
    //   t2v: UNETLoader → KSampler ← (CLIPLoader type=wan → CLIPTextEncode ×2,
    //         EmptyHunyuanLatentVideo) → VAEDecode → SaveAnimatedWEBP
    //   i2v: navíc CLIPVisionLoader + CLIPVisionEncode + LoadImage + WanImageToVideo
    //         (ten produkuje positive/negative/latent pro KSampler).

    /// <summary>Sdílený text encoder (umT5-XXL) — leží v <c>text_encoders/</c>.</summary>
    public const string DefaultWanTextEncoder = "umt5_xxl_fp8_e4m3fn_scaled.safetensors";
    /// <summary>Wan VAE — leží ve <c>vae/</c>.</summary>
    public const string DefaultWanVae         = "wan_2.1_vae.safetensors";
    /// <summary>CLIP-Vision (jen pro image→video) — leží v <c>clip_vision/</c>.</summary>
    public const string DefaultWanClipVision  = "clip_vision_h.safetensors";
    /// <summary>Doporučený sampler pro Wan (oficiální example).</summary>
    public const string DefaultWanSampler     = "uni_pc";
    /// <summary>Doporučený scheduler pro Wan.</summary>
    public const string DefaultWanScheduler   = "simple";
    /// <summary>Výchozí FPS výstupního WEBP (oficiální example).</summary>
    public const int    DefaultWanFps         = 16;

    /// <summary>Oficiální negativní prompt z Wan example workflow (kvalita/artefakty).</summary>
    public const string DefaultWanNegative =
        "Overexposure, static, blurred details, subtitles, paintings, pictures, still, " +
        "overall gray, worst quality, low quality, JPEG compression residue, ugly, mutilated, " +
        "redundant fingers, poorly painted hands, poorly painted faces, deformed, disfigured, " +
        "deformed limbs, fused fingers, cluttered background, three legs, " +
        "a lot of people in the background, upside down";

    /// <summary>ID KSampler uzlu ve Wan workflow (pro sledování progresu).</summary>
    public const string WanKSamplerKey = "3";
    /// <summary>ID výstupního uzlu (VHS_VideoCombine / SaveAnimatedWEBP) ve Wan workflow.</summary>
    public const string WanSaveKey     = "28";

    /// <summary>VideoHelperSuite formát pro MP4 (H.264) — sdílitelný výstup.</summary>
    public const string WanOutputMp4  = "video/h264-mp4";
    /// <summary>Animovaný WEBP — nativní ComfyUI výstup bez ffmpeg/custom nodu.</summary>
    public const string WanOutputWebp = "webp";

    /// <summary>
    /// Sestaví výstupní (save) uzel pro Wan workflow podle <paramref name="outputFormat"/>:
    /// MP4 přes <c>VHS_VideoCombine</c> (ComfyUI-VideoHelperSuite — nutný ffmpeg + custom node),
    /// jinak nativní <c>SaveAnimatedWEBP</c>. Vstup obrázků bere z VAEDecode (uzel "8").
    /// </summary>
    private static Dictionary<string, object> WanOutputNode(
        string outputFormat, string filenamePrefix, int fps)
    {
        if (string.Equals(outputFormat, WanOutputWebp, StringComparison.OrdinalIgnoreCase))
        {
            return Node("SaveAnimatedWEBP", new()
            {
                ["filename_prefix"] = filenamePrefix,
                ["images"]          = Ref("8", 0),
                ["fps"]             = fps,
                ["lossless"]        = false,
                ["quality"]         = 90,
                ["method"]          = "default",
            });
        }

        // MP4 (H.264) přes VideoHelperSuite — yuv420p kvůli kompatibilitě přehrávačů.
        return Node("VHS_VideoCombine", new()
        {
            ["frame_rate"]      = fps,
            ["loop_count"]      = 0,
            ["filename_prefix"] = filenamePrefix,
            ["format"]          = WanOutputMp4,
            ["pix_fmt"]         = "yuv420p",
            ["crf"]             = 19,
            ["save_metadata"]   = true,
            ["pingpong"]        = false,
            ["save_output"]     = true,
            ["images"]          = Ref("8", 0),
        });
    }

    /// <summary>
    /// Wan 2.1 <b>text→video</b> workflow. <paramref name="diffusionModel"/> je soubor v
    /// <c>diffusion_models/</c> (např. <c>wan2.1_t2v_1.3B_bf16.safetensors</c> nebo 14B).
    /// <paramref name="length"/> je počet snímků (např. 33 ≈ 2 s při 16 FPS).
    /// </summary>
    public static Dictionary<string, object> BuildWanTextToVideo(
        string diffusionModel,
        string prompt,
        int    width,
        int    height,
        int    length,
        int    steps,
        double cfg,
        long   seed,
        string negativePrompt = DefaultWanNegative,
        string textEncoder    = DefaultWanTextEncoder,
        string vae            = DefaultWanVae,
        string sampler        = DefaultWanSampler,
        string scheduler      = DefaultWanScheduler,
        int    fps            = DefaultWanFps,
        string outputFormat   = WanOutputMp4,
        string filenamePrefix = "AIStudio")
    {
        return new Dictionary<string, object>
        {
            ["37"] = Node("UNETLoader", new()
            {
                ["unet_name"]    = diffusionModel,
                ["weight_dtype"] = "default",
            }),
            ["38"] = Node("CLIPLoader", new()
            {
                ["clip_name"] = textEncoder,
                ["type"]      = "wan",
            }),
            ["39"] = Node("VAELoader", new()
            {
                ["vae_name"] = vae,
            }),
            ["6"] = Node("CLIPTextEncode", new()
            {
                ["text"] = prompt,
                ["clip"] = Ref("38", 0),
            }),
            ["7"] = Node("CLIPTextEncode", new()
            {
                ["text"] = negativePrompt,
                ["clip"] = Ref("38", 0),
            }),
            ["40"] = Node("EmptyHunyuanLatentVideo", new()
            {
                ["width"]      = width,
                ["height"]     = height,
                ["length"]     = length,
                ["batch_size"] = 1,
            }),
            [WanKSamplerKey] = Node("KSampler", new()
            {
                ["seed"]         = seed,
                ["steps"]        = steps,
                ["cfg"]          = cfg,
                ["sampler_name"] = sampler,
                ["scheduler"]    = scheduler,
                ["denoise"]      = 1.0,
                ["model"]        = Ref("37", 0),
                ["positive"]     = Ref("6", 0),
                ["negative"]     = Ref("7", 0),
                ["latent_image"] = Ref("40", 0),
            }),
            ["8"] = Node("VAEDecode", new()
            {
                ["samples"] = Ref(WanKSamplerKey, 0),
                ["vae"]     = Ref("39", 0),
            }),
            [WanSaveKey] = WanOutputNode(outputFormat, filenamePrefix, fps),
        };
    }

    /// <summary>
    /// Wan 2.1 <b>image→video</b> workflow. <paramref name="diffusionModel"/> je i2v soubor
    /// (např. <c>wan2.1_i2v_480p_14B_bf16.safetensors</c>; pro Wan 2.1 i2v existuje jen 14B).
    /// <paramref name="uploadedImageName"/> je název vrácený z ComfyUI <c>/upload/image</c>.
    /// </summary>
    public static Dictionary<string, object> BuildWanImageToVideo(
        string diffusionModel,
        string uploadedImageName,
        string prompt,
        int    width,
        int    height,
        int    length,
        int    steps,
        double cfg,
        long   seed,
        string negativePrompt = DefaultWanNegative,
        string textEncoder    = DefaultWanTextEncoder,
        string vae            = DefaultWanVae,
        string clipVision     = DefaultWanClipVision,
        string sampler        = DefaultWanSampler,
        string scheduler      = DefaultWanScheduler,
        int    fps            = DefaultWanFps,
        string outputFormat   = WanOutputMp4,
        string filenamePrefix = "AIStudio")
    {
        return new Dictionary<string, object>
        {
            ["37"] = Node("UNETLoader", new()
            {
                ["unet_name"]    = diffusionModel,
                ["weight_dtype"] = "default",
            }),
            ["38"] = Node("CLIPLoader", new()
            {
                ["clip_name"] = textEncoder,
                ["type"]      = "wan",
            }),
            ["39"] = Node("VAELoader", new()
            {
                ["vae_name"] = vae,
            }),
            ["49"] = Node("CLIPVisionLoader", new()
            {
                ["clip_name"] = clipVision,
            }),
            ["52"] = Node("LoadImage", new()
            {
                ["image"] = uploadedImageName,
            }),
            ["6"] = Node("CLIPTextEncode", new()
            {
                ["text"] = prompt,
                ["clip"] = Ref("38", 0),
            }),
            ["7"] = Node("CLIPTextEncode", new()
            {
                ["text"] = negativePrompt,
                ["clip"] = Ref("38", 0),
            }),
            ["51"] = Node("CLIPVisionEncode", new()
            {
                ["clip_vision"] = Ref("49", 0),
                ["image"]       = Ref("52", 0),
                ["crop"]        = "center",
            }),
            ["50"] = Node("WanImageToVideo", new()
            {
                ["positive"]           = Ref("6", 0),
                ["negative"]           = Ref("7", 0),
                ["vae"]                = Ref("39", 0),
                ["clip_vision_output"] = Ref("51", 0),
                ["start_image"]        = Ref("52", 0),
                ["width"]              = width,
                ["height"]             = height,
                ["length"]             = length,
                ["batch_size"]         = 1,
            }),
            [WanKSamplerKey] = Node("KSampler", new()
            {
                ["seed"]         = seed,
                ["steps"]        = steps,
                ["cfg"]          = cfg,
                ["sampler_name"] = sampler,
                ["scheduler"]    = scheduler,
                ["denoise"]      = 1.0,
                ["model"]        = Ref("37", 0),
                ["positive"]     = Ref("50", 0),
                ["negative"]     = Ref("50", 1),
                ["latent_image"] = Ref("50", 2),
            }),
            ["8"] = Node("VAEDecode", new()
            {
                ["samples"] = Ref(WanKSamplerKey, 0),
                ["vae"]     = Ref("39", 0),
            }),
            [WanSaveKey] = WanOutputNode(outputFormat, filenamePrefix, fps),
        };
    }

    /// <summary>
    /// Vloží LoRA do Wan workflow (text→video i obrázek→video). Wan LoRA působí jen na
    /// difúzní model (UNET) — text encoder umT5 se neLoRAuje — proto řetězíme
    /// <c>LoraLoaderModelOnly</c> mezi <c>UNETLoader</c> ("37") a vstup "model" KSampleru.
    /// Prázdný seznam = beze změny.
    /// </summary>
    public static void InjectWanLoras(Dictionary<string, object> workflow, IReadOnlyList<LoraItem> loras)
    {
        if (loras is null || loras.Count == 0) return;

        object modelRef = Ref("37", 0);
        var id = 60;   // mimo rozsah Wan uzlů (3,6,7,8,28,37,38,39,40,49,50,51,52)
        foreach (var lora in loras)
        {
            var key = id.ToString();
            workflow[key] = Node("LoraLoaderModelOnly", new()
            {
                ["model"]          = modelRef,
                ["lora_name"]      = lora.Name,
                ["strength_model"] = lora.StrengthModel,
            });
            modelRef = Ref(key, 0);
            id++;
        }

        // Přepoj KSampler "model" na poslední LoRA uzel.
        if (workflow.TryGetValue(WanKSamplerKey, out var ksObj) &&
            ksObj is Dictionary<string, object> ks &&
            ks.TryGetValue("inputs", out var inObj) &&
            inObj is Dictionary<string, object> ksIn)
        {
            ksIn["model"] = modelRef;
        }
    }

    // ── IMG2IMG – SDXL / SD ───────────────────────────────────────────────────

    /// <summary>
    /// Img2img workflow pro SD/SDXL checkpointy.
    /// <paramref name="uploadedImageName"/> je název souboru vrácený z ComfyUI /upload/image API.
    /// <paramref name="denoise"/> řídí míru přepracování: 0.0 = kopie předlohy, 1.0 = čistý txt2img.
    /// Pro „inspiraci" bez kopírování doporučujeme 0.65–0.85.
    /// </summary>
    public static Dictionary<string, object> BuildStandardImg2Img(
        string checkpoint,
        string uploadedImageName,
        string prompt,
        string negativePrompt,
        int    width,
        int    height,
        int    steps,
        double cfg,
        long   seed,
        double denoise,
        int    batchSize = 1,
        string sampler   = DefaultSamplerSd,
        string scheduler = DefaultSchedulerSd)
    {
        return new Dictionary<string, object>
        {
            ["1"] = Node("CheckpointLoaderSimple", new()
            {
                ["ckpt_name"] = checkpoint,
            }),
            // Načtení referenčního obrázku z ComfyUI input složky
            ["2"] = Node("LoadImage", new()
            {
                ["image"] = uploadedImageName,
                ["upload"] = "image",
            }),
            // Přeškálování na cílové rozlišení — zachová kompozici, ořeže okraje
            ["3"] = Node("ImageScale", new()
            {
                ["image"]          = Ref("2", 0),
                ["width"]          = width,
                ["height"]         = height,
                ["upscale_method"] = "lanczos",
                ["crop"]           = "center",
            }),
            // Enkódování do latentního prostoru VAE
            ["4"] = Node("VAEEncode", new()
            {
                ["pixels"] = Ref("3", 0),
                ["vae"]    = Ref("1", 2),
            }),
            ["5"] = Node("CLIPTextEncode", new()
            {
                ["text"] = prompt,
                ["clip"] = Ref("1", 1),
            }),
            ["6"] = Node("CLIPTextEncode", new()
            {
                ["text"] = negativePrompt,
                ["clip"] = Ref("1", 1),
            }),
            ["7"] = Node("KSampler", new()
            {
                ["seed"]          = seed,
                ["steps"]         = steps,
                ["cfg"]           = cfg,
                ["sampler_name"]  = sampler,
                ["scheduler"]     = scheduler,
                ["denoise"]       = denoise,   // klíčový parametr!
                ["model"]         = Ref("1", 0),
                ["positive"]      = Ref("5", 0),
                ["negative"]      = Ref("6", 0),
                ["latent_image"]  = Ref("4", 0),
            }),
            ["8"] = Node("VAEDecode", new()
            {
                ["samples"] = Ref("7", 0),
                ["vae"]     = Ref("1", 2),
            }),
            ["9"] = Node("SaveImage", new()
            {
                ["filename_prefix"] = "AIStudio",
                ["images"]          = Ref("8", 0),
            }),
        };
    }

    // ── IMG2IMG – FLUX ────────────────────────────────────────────────────────

    /// <summary>
    /// Img2img workflow pro FLUX.1 Dev / Schnell safetensors checkpointy.
    /// FLUX používá FluxGuidance místo cfg — <paramref name="guidance"/> řídí sílu promptu.
    /// </summary>
    public static Dictionary<string, object> BuildFluxImg2Img(
        string checkpoint,
        string uploadedImageName,
        string prompt,
        int    width,
        int    height,
        int    steps,
        double guidance,
        long   seed,
        double denoise,
        int    batchSize = 1,
        string sampler   = DefaultSamplerFlux,
        string scheduler = DefaultSchedulerFlux)
    {
        return new Dictionary<string, object>
        {
            ["1"] = Node("CheckpointLoaderSimple", new()
            {
                ["ckpt_name"] = checkpoint,
            }),
            ["2"] = Node("LoadImage", new()
            {
                ["image"]  = uploadedImageName,
                ["upload"] = "image",
            }),
            ["3"] = Node("ImageScale", new()
            {
                ["image"]          = Ref("2", 0),
                ["width"]          = width,
                ["height"]         = height,
                ["upscale_method"] = "lanczos",
                ["crop"]           = "center",
            }),
            ["4"] = Node("VAEEncode", new()
            {
                ["pixels"] = Ref("3", 0),
                ["vae"]    = Ref("1", 2),
            }),
            ["5"] = Node("CLIPTextEncode", new()
            {
                ["text"] = prompt,
                ["clip"] = Ref("1", 1),
            }),
            ["6"] = Node("CLIPTextEncode", new()
            {
                ["text"] = "",
                ["clip"] = Ref("1", 1),
            }),
            ["7"] = Node("FluxGuidance", new()
            {
                ["conditioning"] = Ref("5", 0),
                ["guidance"]     = guidance,
            }),
            ["8"] = Node("KSampler", new()
            {
                ["seed"]         = seed,
                ["steps"]        = steps,
                ["cfg"]          = 1.0,
                ["sampler_name"] = sampler,
                ["scheduler"]    = scheduler,
                ["denoise"]      = denoise,
                ["model"]        = Ref("1", 0),
                ["positive"]     = Ref("7", 0),
                ["negative"]     = Ref("6", 0),
                ["latent_image"] = Ref("4", 0),
            }),
            ["9"] = Node("VAEDecode", new()
            {
                ["samples"] = Ref("8", 0),
                ["vae"]     = Ref("1", 2),
            }),
            ["10"] = Node("SaveImage", new()
            {
                ["filename_prefix"] = "AIStudio",
                ["images"]          = Ref("9", 0),
            }),
        };
    }

    /// <summary>
    /// Převádí "kreativní volnost" (0–1) na denoise hodnotu pro img2img.
    /// 0.0 = výsledek blízký referenci (denoise ~0.50)
    /// 1.0 = výsledek blízký promptu, reference jen inspiruje (denoise ~0.97)
    /// </summary>
    public static double CreativityToDenoise(double creativity)
        => Math.Clamp(0.50 + creativity * 0.47, 0.50, 0.97);

    // ── FLUX.1 Kontext (instrukční editace) ──────────────────────────────────

    /// <summary>
    /// FLUX.1 Kontext [dev] — instrukční editace obrázku. Na rozdíl od klasického
    /// img2img (kde referenci jen zašumíme a denoise rozhoduje o věrnosti) Kontext
    /// vkládá referenci do CONDITIONINGU přes uzel <c>ReferenceLatent</c> — model
    /// „vidí" originál a aplikuje textovou instrukci („dej mu klobouk", „udělej z
    /// toho noční scénu") při zachování zbytku obrázku. Nejbližší lokální ekvivalent
    /// editace obrázku v ChatGPT.
    ///
    /// <para>Vyžaduje separátní soubory (jako GGUF): UNET
    /// (<c>flux1-dev-kontext_fp8_scaled.safetensors</c>), DualCLIP (clip_l + t5xxl)
    /// a VAE (<c>ae.safetensors</c>) — ty samé, co sdílí ostatní FLUX workflow.</para>
    ///
    /// <para>Reference se VAE-enkóduje jednou a použije <b>jak</b> pro
    /// <c>ReferenceLatent</c>, <b>tak</b> jako <c>latent_image</c> KSampleru s
    /// <c>denoise=1.0</c> — tím výstup automaticky drží rozměry vstupu, aniž
    /// bychom při buildu znali scaled rozlišení z <c>FluxKontextImageScale</c>.</para>
    /// </summary>
    public static Dictionary<string, object> BuildFluxKontext(
        string unetFile,
        string clipLFile,
        string t5File,
        string vaeFile,
        string uploadedImageName,
        string instructionPrompt,
        int    steps,
        double guidance,
        long   seed,
        string sampler   = DefaultSamplerFlux,
        string scheduler = DefaultSchedulerFlux)
    {
        return new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new()
            {
                ["unet_name"]    = unetFile,
                ["weight_dtype"] = "default",
            }),
            ["2"] = Node("DualCLIPLoader", new()
            {
                ["clip_name1"] = clipLFile,
                ["clip_name2"] = t5File,
                ["type"]       = "flux",
            }),
            ["3"] = Node("VAELoader", new()
            {
                ["vae_name"] = vaeFile,
            }),
            ["4"] = Node("LoadImage", new()
            {
                ["image"]  = uploadedImageName,
                ["upload"] = "image",
            }),
            // Kontext byl trénovaný na konkrétních rozlišeních — přeškáluje vstup
            // na nejbližší podporovaný bucket (lepší kvalita než libovolný rozměr).
            ["5"] = Node("FluxKontextImageScale", new()
            {
                ["image"] = Ref("4", 0),
            }),
            ["6"] = Node("VAEEncode", new()
            {
                ["pixels"] = Ref("5", 0),
                ["vae"]    = Ref("3", 0),
            }),
            ["7"] = Node("CLIPTextEncode", new()
            {
                ["text"] = instructionPrompt,
                ["clip"] = Ref("2", 0),
            }),
            // ReferenceLatent vloží originál do conditioningu — srdce Kontextu.
            ["8"] = Node("ReferenceLatent", new()
            {
                ["conditioning"] = Ref("7", 0),
                ["latent"]       = Ref("6", 0),
            }),
            ["9"] = Node("FluxGuidance", new()
            {
                ["conditioning"] = Ref("8", 0),
                ["guidance"]     = guidance,
            }),
            // FLUX jede cfg=1.0 → negative se nepoužije, ale KSampler ho vyžaduje.
            ["10"] = Node("CLIPTextEncode", new()
            {
                ["text"] = "",
                ["clip"] = Ref("2", 0),
            }),
            ["11"] = Node("KSampler", new()
            {
                ["seed"]         = seed,
                ["steps"]        = steps,
                ["cfg"]          = 1.0,
                ["sampler_name"] = sampler,
                ["scheduler"]    = scheduler,
                ["denoise"]      = 1.0,
                ["model"]        = Ref("1", 0),
                ["positive"]     = Ref("9", 0),
                ["negative"]     = Ref("10", 0),
                ["latent_image"] = Ref("6", 0),
            }),
            ["12"] = Node("VAEDecode", new()
            {
                ["samples"] = Ref("11", 0),
                ["vae"]     = Ref("3", 0),
            }),
            ["13"] = Node("SaveImage", new()
            {
                ["filename_prefix"] = "AIStudio",
                ["images"]          = Ref("12", 0),
            }),
        };
    }

    // ── LoRA injection ────────────────────────────────────────────────────────

    /// <summary>
    /// Vloží řetěz LoraLoader uzlů a přepojí spotřebitele modelu a clipu
    /// na výstupy posledního LoRA uzlu.
    ///
    /// Overload pro checkpointy, kde jedna node vrací model (output 0) i clip (output 1):
    /// Standard SD/SDXL, FLUX safetensors a jejich img2img varianty.
    /// </summary>
    /// <param name="workflow">Workflow ke změně (in-place).</param>
    /// <param name="checkpointKey">ID uzlu (CheckpointLoaderSimple); výstupy 0=model, 1=clip.</param>
    /// <param name="modelConsumerKeys">Uzly, jejichž vstup "model" se přepojí na poslední LoRA.</param>
    /// <param name="clipConsumerKeys">Uzly, jejichž vstup "clip" se přepojí na poslední LoRA.</param>
    /// <param name="loras">LoRA adaptery k vložení (v pořadí).</param>
    public static void InjectLoras(
        Dictionary<string, object> workflow,
        string                     checkpointKey,
        IReadOnlyList<string>      modelConsumerKeys,
        IReadOnlyList<string>      clipConsumerKeys,
        IReadOnlyList<LoraItem>    loras)
        => InjectLoras(workflow, Ref(checkpointKey, 0), Ref(checkpointKey, 1),
                       modelConsumerKeys, clipConsumerKeys, loras);

    /// <summary>
    /// Overload pro GGUF workflow, kde model (UnetLoaderGGUF) a clip (DualCLIPLoader)
    /// přicházejí z <b>oddělených</b> uzlů — každý poskytuje jen jeden výstup.
    /// </summary>
    /// <param name="initialModelRef">Odkaz na výstup model-loaderu (Ref("1",0) pro GGUF).</param>
    /// <param name="initialClipRef">Odkaz na výstup clip-loaderu (Ref("2",0) pro GGUF).</param>
    public static void InjectLoras(
        Dictionary<string, object> workflow,
        object                     initialModelRef,
        object                     initialClipRef,
        IReadOnlyList<string>      modelConsumerKeys,
        IReadOnlyList<string>      clipConsumerKeys,
        IReadOnlyList<LoraItem>    loras)
    {
        if (loras is null || loras.Count == 0) return;

        // Vyhradíme ID rozsah 50–59 pro LoRA uzly (mimo rozsah build metod 1–10
        // a mimo 100+ používaný pro reference images injection).
        var modelRef = initialModelRef;
        var clipRef  = initialClipRef;
        var nextId   = 50;

        foreach (var lora in loras)
        {
            var id = (nextId++).ToString();
            workflow[id] = Node("LoraLoader", new()
            {
                ["model"]          = modelRef,
                ["clip"]           = clipRef,
                ["lora_name"]      = lora.Name,
                ["strength_model"] = lora.StrengthModel,
                ["strength_clip"]  = lora.StrengthClip,
            });
            modelRef = Ref(id, 0);
            clipRef  = Ref(id, 1);
        }

        // Přepoj spotřebitele na výstupy posledního LoRA uzlu
        foreach (var key in modelConsumerKeys)
            SetInput(workflow, key, "model", modelRef);

        foreach (var key in clipConsumerKeys)
            SetInput(workflow, key, "clip", clipRef);
    }

    // Source refs pro GGUF workflow (UnetLoaderGGUF="1", DualCLIPLoader="2")
    /// <summary>Model output ref pro FLUX GGUF workflow (UnetLoaderGGUF, výstup 0).</summary>
    public static object GgufModelRef => Ref("1", 0);
    /// <summary>Clip output ref pro FLUX GGUF workflow (DualCLIPLoader, výstup 0).</summary>
    public static object GgufClipRef  => Ref("2", 0);

    private static void SetInput(
        Dictionary<string, object> workflow, string nodeKey,
        string inputName, object value)
    {
        if (workflow.TryGetValue(nodeKey, out var raw)
            && raw is Dictionary<string, object> node
            && node.TryGetValue("inputs", out var inp)
            && inp is Dictionary<string, object> inputs)
        {
            inputs[inputName] = value;
        }
    }

    /// <summary>Najde ID prvního uzlu daného <c>class_type</c> (workflow má od každého typu jeden).</summary>
    private static string? FindNodeByClass(Dictionary<string, object> workflow, string classType)
    {
        foreach (var (key, raw) in workflow)
            if (raw is Dictionary<string, object> node
                && node.TryGetValue("class_type", out var ct)
                && ct is string s && s == classType)
                return key;
        return null;
    }

    /// <summary>Vrátí inputs dictionary uzlu, nebo null.</summary>
    private static Dictionary<string, object>? GetInputs(Dictionary<string, object> workflow, string nodeKey)
        => workflow.TryGetValue(nodeKey, out var raw)
           && raw is Dictionary<string, object> node
           && node.TryGetValue("inputs", out var inp)
           && inp is Dictionary<string, object> inputs
            ? inputs : null;

    // ── Upscale (hires fix + ESRGAN) ─────────────────────────────────────────

    /// <summary>Výchozí ESRGAN upscale model (4×, veřejný RealESRGAN).</summary>
    public const string DefaultUpscaleModel = "RealESRGAN_x4plus.pth";

    /// <summary>
    /// Připojí k libovolnému txt2img/img2img workflow „upscale tail" pro vyšší
    /// kvalitu a detail:
    ///   1) <b>Hires fix</b> — LatentUpscale (×<paramref name="hiresScale"/>) + 2. KSampler
    ///      s nízkým <paramref name="hiresDenoise"/> → model „domaluje" detaily ve vyšším
    ///      rozlišení (pleť, vlasy, oči, hloubka).
    ///   2) <b>ESRGAN</b> (volitelně) — UpscaleModelLoader + ImageUpscaleWithModel a finální
    ///      doškálování na ×<paramref name="finalScale"/> (rozlišení + ostrost).
    ///
    /// <para>Uzly KSampler / VAEDecode / SaveImage hledá podle <c>class_type</c> (každý
    /// workflow má od každého právě jeden v okamžiku volání), takže je univerzální pro
    /// Standard / Flux / FluxGguf / FluxUnet / Kontext i jejich img2img varianty.
    /// Model, positive, negative, vae i sampler/scheduler/cfg/seed přebírá z existujícího
    /// KSampleru — proto respektuje i už injektované LoRA (volej AŽ po InjectLoras).</para>
    /// </summary>
    public static void AppendUpscale(
        Dictionary<string, object> workflow,
        int     baseWidth,
        int     baseHeight,
        double  hiresScale      = 1.5,
        double  hiresDenoise    = 0.35,
        bool    useUpscaleModel = false,
        string? upscaleModelName = null,
        double  finalScale      = 2.0)
    {
        var ksKey = FindNodeByClass(workflow, "KSampler");
        var vdKey = FindNodeByClass(workflow, "VAEDecode");
        var siKey = FindNodeByClass(workflow, "SaveImage");
        if (ksKey is null || vdKey is null || siKey is null) return;

        var ksIn = GetInputs(workflow, ksKey);
        var vdIn = GetInputs(workflow, vdKey);
        if (ksIn is null || vdIn is null) return;
        if (!ksIn.TryGetValue("model", out var modelRef) ||
            !ksIn.TryGetValue("positive", out var posRef) ||
            !ksIn.TryGetValue("negative", out var negRef) ||
            !vdIn.TryGetValue("vae", out var vaeRef))
            return;

        var sampler   = ksIn.TryGetValue("sampler_name", out var sm) ? sm : DefaultSamplerFlux;
        var scheduler = ksIn.TryGetValue("scheduler",    out var sc) ? sc : DefaultSchedulerFlux;
        var cfg       = ksIn.TryGetValue("cfg",          out var cf) ? cf : 1.0;
        var seed      = ksIn.TryGetValue("seed",         out var sd) ? sd : (object)0L;
        var steps     = ksIn.TryGetValue("steps",        out var st) ? st : (object)20;

        var id = 200;
        string NextId() => (id++).ToString();

        var hiresW = (int)Math.Round(baseWidth  * hiresScale);
        var hiresH = (int)Math.Round(baseHeight * hiresScale);

        // 1) Hires fix — upscale latentu z 1. KSampleru + 2. průchod s nízkým denoise.
        var luKey = NextId();
        workflow[luKey] = Node("LatentUpscale", new()
        {
            ["samples"]        = Ref(ksKey, 0),
            ["upscale_method"] = "nearest-exact",
            ["width"]          = hiresW,
            ["height"]         = hiresH,
            ["crop"]           = "disabled",
        });

        var ks2Key = NextId();
        workflow[ks2Key] = Node("KSampler", new()
        {
            ["seed"]         = seed,
            ["steps"]        = steps,
            ["cfg"]          = cfg,
            ["sampler_name"] = sampler,
            ["scheduler"]    = scheduler,
            ["denoise"]      = hiresDenoise,   // jen „domaluje" detail, nepřekreslí kompozici
            ["model"]        = modelRef,
            ["positive"]     = posRef,
            ["negative"]     = negRef,
            ["latent_image"] = Ref(luKey, 0),
        });

        var vd2Key = NextId();
        workflow[vd2Key] = Node("VAEDecode", new()
        {
            ["samples"] = Ref(ks2Key, 0),
            ["vae"]     = vaeRef,
        });

        object finalImage = Ref(vd2Key, 0);

        // 2) ESRGAN model upscaler (volitelně) + finální doškálování na ×finalScale.
        if (useUpscaleModel && !string.IsNullOrWhiteSpace(upscaleModelName))
        {
            var umlKey = NextId();
            workflow[umlKey] = Node("UpscaleModelLoader", new() { ["model_name"] = upscaleModelName });

            var iuKey = NextId();
            workflow[iuKey] = Node("ImageUpscaleWithModel", new()
            {
                ["upscale_model"] = Ref(umlKey, 0),
                ["image"]         = finalImage,
            });

            // ESRGAN je typicky 4× → doškálujeme zpět na cílové base×finalScale (lanczos).
            var finalW = (int)Math.Round(baseWidth  * finalScale);
            var finalH = (int)Math.Round(baseHeight * finalScale);
            var scKey = NextId();
            workflow[scKey] = Node("ImageScale", new()
            {
                ["image"]          = Ref(iuKey, 0),
                ["upscale_method"] = "lanczos",
                ["width"]          = finalW,
                ["height"]         = finalH,
                ["crop"]           = "disabled",
            });
            finalImage = Ref(scKey, 0);
        }

        // 3) Přesměruj SaveImage na finální (upscalovaný) obrázek.
        SetInput(workflow, siKey, "images", finalImage);
    }

    // ── Helpers pro detekci typu modelu ──────────────────────────────────────

    public static bool IsFluxModel(string modelName) =>
        modelName.Contains("FLUX",  StringComparison.OrdinalIgnoreCase) ||
        modelName.Contains("flux",  StringComparison.OrdinalIgnoreCase);

    /// <summary>True pro GGUF kvantizace — vyžadují <c>UnetLoaderGGUF</c> custom node.</summary>
    public static bool IsGgufModel(string modelName) =>
        modelName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True pro FLUX.1 Kontext model — instrukční editace přes <c>ReferenceLatent</c>
    /// workflow (<see cref="BuildFluxKontext"/>), ne klasický denoise img2img.
    /// </summary>
    public static bool IsKontextModel(string modelName) =>
        modelName.Contains("kontext", StringComparison.OrdinalIgnoreCase);

    /// <summary>Výchozí steps + guidance pro FLUX Kontext editaci (dev varianta).</summary>
    public static (int Steps, double Guidance) KontextDefaults => (20, 2.5);

    /// <summary>Odhadne rozumné výchozí hodnoty steps a guidance pro FLUX Schnell vs Dev.</summary>
    public static (int Steps, double Guidance) FluxDefaults(string modelName) =>
        modelName.Contains("Schnell", StringComparison.OrdinalIgnoreCase) ||
        modelName.Contains("schnell", StringComparison.OrdinalIgnoreCase)
            ? (4, 0.0)      // Schnell: 4 kroky, bez guidance
            : (20, 3.5);    // Dev: 20 kroků, guidance 3.5

    // ── PuLID-Flux (identita osoby bez tréninku) ─────────────────────────────

    /// <summary>Soubor PuLID-Flux modelu (ComfyUI/models/pulid/).</summary>
    public const string DefaultPulidFluxFile = "pulid_flux_v0.9.1.safetensors";

    /// <summary>
    /// FLUX txt2img s PuLID identitou — z referenční fotky obličeje vygeneruje
    /// osobu v nové scéně dle promptu, BEZ tréninku LoRA. Nejbližší lokální
    /// ekvivalent ChatGPT „nahraj fotky osoby → vytvoř ji jinde".
    ///
    /// <para>Vyžaduje custom node <c>ComfyUI_PuLID_Flux_ll</c> + modely: PuLID-Flux
    /// (models/pulid/), EVA-CLIP, InsightFace antelopev2. <c>ApplyPulidFlux</c>
    /// modifikuje FLUX model embeddingem obličeje z reference; zbytek je standardní
    /// FLUX txt2img.</para>
    ///
    /// <para>POZOR: přesné názvy vstupů PuLID nodů (model/pulid_flux/eva_clip/
    /// face_analysis/image/weight/start_at/end_at) odpovídají balazik/lldacing
    /// implementaci — runtime ověření nutné, custom node API se může lišit dle verze.</para>
    /// </summary>
    public static Dictionary<string, object> BuildFluxPuLID(
        string unetFile,
        string clipLFile,
        string t5File,
        string vaeFile,
        string pulidFile,
        string uploadedFaceImage,
        string prompt,
        int    width,
        int    height,
        int    steps,
        double guidance,
        long   seed,
        double identityWeight = 0.9,
        string sampler   = DefaultSamplerFlux,
        string scheduler = DefaultSchedulerFlux)
    {
        return new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new()
            {
                ["unet_name"]    = unetFile,
                ["weight_dtype"] = "default",
            }),
            ["2"] = Node("DualCLIPLoader", new()
            {
                ["clip_name1"] = clipLFile,
                ["clip_name2"] = t5File,
                ["type"]       = "flux",
            }),
            ["3"] = Node("VAELoader", new() { ["vae_name"] = vaeFile }),

            // ── PuLID stack ──
            ["4"] = Node("PulidFluxModelLoader", new() { ["pulid_file"] = pulidFile }),
            ["5"] = Node("PulidFluxEvaClipLoader", new()),
            // provider="CPU" — ComfyUI portable má jen onnxruntime (CPU); detekce
            // obličeje (antelopev2) je malá a na CPU rychlá, hlavní PuLID výpočet
            // jede stejně na GPU přes torch. "CUDA" by spadlo (chybí CUDAExecutionProvider).
            ["6"] = Node("PulidFluxInsightFaceLoader", new() { ["provider"] = "CPU" }),
            ["7"] = Node("LoadImage", new()
            {
                ["image"]  = uploadedFaceImage,
                ["upload"] = "image",
            }),
            // ApplyPulidFlux vloží identitu obličeje do FLUX modelu.
            ["8"] = Node("ApplyPulidFlux", new()
            {
                ["model"]         = Ref("1", 0),
                ["pulid_flux"]    = Ref("4", 0),
                ["eva_clip"]      = Ref("5", 0),
                ["face_analysis"] = Ref("6", 0),
                ["image"]         = Ref("7", 0),
                ["weight"]        = identityWeight,
                ["start_at"]      = 0.0,
                ["end_at"]        = 1.0,
            }),

            // ── Standardní FLUX txt2img na modifikovaném modelu ──
            ["9"]  = Node("CLIPTextEncode", new() { ["text"] = prompt, ["clip"] = Ref("2", 0) }),
            ["10"] = Node("CLIPTextEncode", new() { ["text"] = "",     ["clip"] = Ref("2", 0) }),
            ["11"] = Node("FluxGuidance",   new() { ["conditioning"] = Ref("9", 0), ["guidance"] = guidance }),
            ["12"] = Node("EmptyLatentImage", new()
            {
                ["width"]      = width,
                ["height"]     = height,
                ["batch_size"] = 1,
            }),
            ["13"] = Node("KSampler", new()
            {
                ["seed"]         = seed,
                ["steps"]        = steps,
                ["cfg"]          = 1.0,
                ["sampler_name"] = sampler,
                ["scheduler"]    = scheduler,
                ["denoise"]      = 1.0,
                ["model"]        = Ref("8", 0),   // model s PuLID identitou
                ["positive"]     = Ref("11", 0),
                ["negative"]     = Ref("10", 0),
                ["latent_image"] = Ref("12", 0),
            }),
            ["14"] = Node("VAEDecode", new() { ["samples"] = Ref("13", 0), ["vae"] = Ref("3", 0) }),
            ["15"] = Node("SaveImage", new() { ["filename_prefix"] = "AIStudio", ["images"] = Ref("14", 0) }),
        };
    }
}
