namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Sestavuje workflow JSON pro ComfyUI API.
/// Podporuje standardní SD/SDXL checkpoint a FLUX checkpoint.
/// </summary>
public static class ComfyWorkflowBuilder
{
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
        int    batchSize = 1)
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
                ["sampler_name"]  = "euler",
                ["scheduler"]     = "normal",
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
        int    batchSize = 1)
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
                ["sampler_name"] = "euler",
                ["scheduler"]    = "simple",
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
        int    batchSize = 1)
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
                ["sampler_name"] = "euler",
                ["scheduler"]    = "simple",
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object> Node(string classType,
                                                    Dictionary<string, object> inputs)
        => new() { ["class_type"] = classType, ["inputs"] = inputs };

    /// <summary>Odkaz na výstup jiného uzlu: ["nodeId", outputIndex].</summary>
    private static object[] Ref(string nodeId, int outputIndex)
        => new object[] { nodeId, outputIndex };

    // ── Helpers pro detekci typu modelu ──────────────────────────────────────

    public static bool IsFluxModel(string modelName) =>
        modelName.Contains("FLUX",  StringComparison.OrdinalIgnoreCase) ||
        modelName.Contains("flux",  StringComparison.OrdinalIgnoreCase);

    /// <summary>True pro GGUF kvantizace — vyžadují <c>UnetLoaderGGUF</c> custom node.</summary>
    public static bool IsGgufModel(string modelName) =>
        modelName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Odhadne rozumné výchozí hodnoty steps a guidance pro FLUX Schnell vs Dev.</summary>
    public static (int Steps, double Guidance) FluxDefaults(string modelName) =>
        modelName.Contains("Schnell", StringComparison.OrdinalIgnoreCase) ||
        modelName.Contains("schnell", StringComparison.OrdinalIgnoreCase)
            ? (4, 0.0)      // Schnell: 4 kroky, bez guidance
            : (20, 3.5);    // Dev: 20 kroků, guidance 3.5
}
