namespace AIStudio.Core.Services;

/// <summary>Režim Wan video modelu — text→video nebo obrázek→video.</summary>
public enum WanVideoMode
{
    TextToVideo,
    ImageToVideo,
}

/// <summary>
/// Jeden stahovatelný soubor Wan závislosti (model / encoder / VAE). <see cref="Subdir"/>
/// je ComfyUI podsložka (text_encoders / vae / clip_vision / diffusion_models), kde ho
/// ComfyUI uvidí přes extra_model_paths.yaml a workflow ho adresuje pouhým názvem.
/// </summary>
public sealed record WanDownloadFile(
    string Label,
    string FileName,
    string Url,
    string Subdir,
    long   SizeBytes);

/// <summary>
/// Wan 2.1 video model (diffusion model) + metadata pro UI a stahování. Sdílené
/// závislosti (text encoder, VAE, příp. CLIP-Vision) doplní <see cref="WanModels.RequiredFiles"/>.
/// </summary>
public sealed record WanVideoModel(
    string       Id,
    string       Label,
    string       Description,
    WanVideoMode Mode,
    WanDownloadFile DiffusionModel,
    bool         NeedsClipVision,
    int          DefaultWidth,
    int          DefaultHeight);

/// <summary>
/// Katalog Wan 2.1 modelů a sdílených závislostí. Vše z veřejného repackage repa
/// <c>Comfy-Org/Wan_2.1_ComfyUI_repackaged</c> (bez HF tokenu). Názvy souborů přesně
/// odpovídají těm, které generuje <see cref="ComfyWorkflowBuilder"/> (CLIPLoader/VAELoader/
/// CLIPVisionLoader/UNETLoader).
/// </summary>
public static class WanModels
{
    /// <summary>Base URL split_files v repackage repu.</summary>
    public const string RepoBase =
        "https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged/resolve/main/split_files/";

    // ── Sdílené závislosti ─────────────────────────────────────────────────────

    public static readonly WanDownloadFile TextEncoder = new(
        Label:    "Text encoder (umT5-XXL)",
        FileName: ComfyWorkflowBuilder.DefaultWanTextEncoder,
        Url:      RepoBase + "text_encoders/" + ComfyWorkflowBuilder.DefaultWanTextEncoder,
        Subdir:   "text_encoders",
        SizeBytes: 6_740_000_000);

    public static readonly WanDownloadFile Vae = new(
        Label:    "VAE",
        FileName: ComfyWorkflowBuilder.DefaultWanVae,
        Url:      RepoBase + "vae/" + ComfyWorkflowBuilder.DefaultWanVae,
        Subdir:   "vae",
        SizeBytes: 254_000_000);

    public static readonly WanDownloadFile ClipVision = new(
        Label:    "CLIP-Vision (obrázek→video)",
        FileName: ComfyWorkflowBuilder.DefaultWanClipVision,
        Url:      RepoBase + "clip_vision/" + ComfyWorkflowBuilder.DefaultWanClipVision,
        Subdir:   "clip_vision",
        SizeBytes: 1_260_000_000);

    // ── Diffusion modely ───────────────────────────────────────────────────────

    private static WanDownloadFile Diff(string file, long size) => new(
        Label:    "Video model",
        FileName: file,
        Url:      RepoBase + "diffusion_models/" + file,
        Subdir:   "diffusion_models",
        SizeBytes: size);

    /// <summary>Text→video 1.3B — rychlé, lehké (~2.8 GB), dobré pro iteraci / slabší HW.</summary>
    public static readonly WanVideoModel T2V_1_3B = new(
        Id:    "wan21-t2v-1.3b",
        Label: "Text → video · 1.3B (rychlé)",
        Description: "Rychlý a lehký model (~2.8 GB). Nižší detail než 14B, ideální na iteraci.",
        Mode:  WanVideoMode.TextToVideo,
        DiffusionModel: Diff("wan2.1_t2v_1.3B_fp16.safetensors", 2_840_000_000),
        NeedsClipVision: false,
        DefaultWidth: 832, DefaultHeight: 480);

    /// <summary>Text→video 14B fp8 — kvalita, vejde se na 24 GB (~14.3 GB).</summary>
    public static readonly WanVideoModel T2V_14B = new(
        Id:    "wan21-t2v-14b",
        Label: "Text → video · 14B (kvalita)",
        Description: "Nejlepší kvalita text→video (fp8, ~14.3 GB). Pomalejší, vyžaduje ~24 GB VRAM.",
        Mode:  WanVideoMode.TextToVideo,
        DiffusionModel: Diff("wan2.1_t2v_14B_fp8_scaled.safetensors", 14_300_000_000),
        NeedsClipVision: false,
        DefaultWidth: 832, DefaultHeight: 480);

    /// <summary>Obrázek→video 480p 14B fp8 — animace vstupního obrázku (~16.4 GB).</summary>
    public static readonly WanVideoModel I2V_480P_14B = new(
        Id:    "wan21-i2v-480p-14b",
        Label: "Obrázek → video · 480p 14B",
        Description: "Rozhýbe vstupní obrázek (fp8, ~16.4 GB). Pro Wan 2.1 i2v existuje jen 14B.",
        Mode:  WanVideoMode.ImageToVideo,
        DiffusionModel: Diff("wan2.1_i2v_480p_14B_fp8_scaled.safetensors", 16_400_000_000),
        NeedsClipVision: true,
        DefaultWidth: 512, DefaultHeight: 512);

    /// <summary>Všechny modely v katalogu (pro UI picker).</summary>
    public static IReadOnlyList<WanVideoModel> All { get; } =
        new[] { T2V_14B, T2V_1_3B, I2V_480P_14B };

    /// <summary>Najde model podle Id, nebo null.</summary>
    public static WanVideoModel? FindById(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Všechny soubory potřebné pro daný model: text encoder + VAE + diffusion model
    /// (+ CLIP-Vision pro obrázek→video). Pořadí = doporučené pořadí stahování
    /// (nejdřív menší sdílené, diffusion model jako poslední).
    /// </summary>
    public static IReadOnlyList<WanDownloadFile> RequiredFiles(WanVideoModel model)
    {
        var list = new List<WanDownloadFile> { Vae, TextEncoder };
        if (model.NeedsClipVision) list.Add(ClipVision);
        list.Add(model.DiffusionModel);
        return list;
    }
}
