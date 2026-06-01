using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Statická knihovna doporučených modelů — hardcoded seznam, který wizard nabízí
/// uživateli. Cíl: po wizardu má uživatel stažený 1 chat + 1 image model
/// vhodný pro jeho hardware, **bez nutnosti ručního výběru**.
///
/// Tiery podle VRAM:
///   • Low tier (&lt; 8 GB) — Llama 3.2 3B + SD 1.5 (DreamShaper). Vejde se
///     do 4 GB VRAM, run-able i na CPU.
///   • Default tier (8 GB+) — Llama 3.1 8B + FLUX Schnell GGUF Q4. Vyšší
///     kvalita, vyžaduje slušnou kartu.
///
/// Kritéria výběru:
///   • Chat: kvalitní v češtině, GGUF Q4_K_M (rovnováha kvalita/velikost),
///     bez gated repa (žádný HF token nutný).
///   • Image: rychlé (Schnell / Turbo), Apache nebo CreativeML Open licence.
///
/// **Pozor:** Pokud HuggingFace přerazí soubor (nezveřejněná verze), URL
/// musí být ručně aktualizována. SHA-256 je null protože bartowski / city96
/// nezveřejňují hashes — DownloadService bez hashe pouze stáhne bez verifikace.
/// </summary>
public static class RecommendedModels
{
    // ── Default tier (8 GB+ VRAM) ────────────────────────────────────────────

    /// <summary>
    /// Llama 3.1 8B Instruct v GGUF Q4_K_M kvantizaci.
    /// Velikost ~4.9 GB, vejde se do RTX 3060/3070 s 8 GB VRAM.
    /// Mirror od bartowski (HF), který má quant skripty + dobré výsledky.
    /// </summary>
    public static readonly RecommendedModel Llama31_8B_Instruct = new(
        Id:                       "llama-3.1-8b-instruct-q4km",
        Name:                     "Llama 3.1 8B Instruct (Q4_K_M)",
        Description:              "Vyvážený chat model — rychlý, dobře umí česky, vejde se do 8 GB VRAM.",
        Kind:                     RecommendedModelKind.Chat,
        SizeBytes:                4_920_000_000L,
        DownloadUrl:              "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF/resolve/main/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
        FileName:                 "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    /// <summary>
    /// FLUX.1 Schnell v GGUF Q4_0 kvantizaci od city96.
    /// Velikost ~6.8 GB. Schnell varianta je rychlá (4 kroky), Apache-2.0 licence.
    /// Vyžaduje samostatné CLIP-L + T5 + VAE soubory (FluxDependencyService je stáhne).
    /// </summary>
    public static readonly RecommendedModel FluxSchnell_Q4 = new(
        Id:                       "flux1-schnell-q4-gguf",
        Name:                     "FLUX.1 Schnell (Q4_0 GGUF)",
        Description:              "Rychlý generátor obrázků — 4 kroky stačí. Apache-2.0 licence, bez registrace.",
        Kind:                     RecommendedModelKind.Image,
        SizeBytes:                6_810_000_000L,
        DownloadUrl:              "https://huggingface.co/city96/FLUX.1-schnell-gguf/resolve/main/flux1-schnell-Q4_0.gguf",
        FileName:                 "flux1-schnell-Q4_0.gguf",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    /// <summary>
    /// FLUX.1 dev v fp8 (UNET-only, ~11.9 GB) — base model pro FLUX LoRA trénink
    /// na sd-scripts. Veřejný Kijai repackage (bez HF tokenu / license gate).
    /// Na rozdíl od FLUX GGUF (netrénovatelný) je tohle plný safetensors, který
    /// flux_train_network.py umí. NENÍ to all-in-one — clip/t5/ae se řeší zvlášť.
    /// </summary>
    public static readonly RecommendedModel FluxDev_Fp8 = new(
        Id:                       "flux1-dev-fp8",
        Name:                     "FLUX.1 dev (fp8) — base pro LoRA trénink",
        Description:              "Plný FLUX.1 dev UNET ve fp8 (~11.9 GB) pro trénink FLUX LoRA. Veřejný, bez tokenu.",
        Kind:                     RecommendedModelKind.Image,
        SizeBytes:                11_901_525_888L,
        DownloadUrl:              "https://huggingface.co/Kijai/flux-fp8/resolve/main/flux1-dev-fp8.safetensors",
        FileName:                 "flux1-dev-fp8.safetensors",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    // ── Low tier (&lt; 8 GB VRAM, integrovaná GPU, CPU-only) ─────────────────

    /// <summary>
    /// Llama 3.2 3B Instruct GGUF Q4_K_M — kompaktní model pro slabší železo.
    /// Velikost ~2 GB, vejde se do 4 GB VRAM nebo poběží na CPU za pár tokenů/s.
    /// Stejná rodina jako 8B verze, jen menší capacita.
    /// </summary>
    public static readonly RecommendedModel Llama32_3B_Instruct = new(
        Id:                       "llama-3.2-3b-instruct-q4km",
        Name:                     "Llama 3.2 3B Instruct (Q4_K_M)",
        Description:              "Kompaktní chat model pro slabší železo (4 GB VRAM / CPU). Méně kvalitní než 8B, ale funguje rychle všude.",
        Kind:                     RecommendedModelKind.Chat,
        SizeBytes:                2_020_000_000L,
        DownloadUrl:              "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf",
        FileName:                 "Llama-3.2-3B-Instruct-Q4_K_M.gguf",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    /// <summary>
    /// DreamShaper 8 — Stable Diffusion 1.5 fine-tune, vyvážený realismus/styl.
    /// Velikost ~2.1 GB safetensors (žádné samostatné CLIP/VAE potřeba — SD 1.5
    /// má vše bundled). CreativeML Open RAIL-M licence, free pro non-commercial.
    /// Vejde se do 4 GB VRAM, 512×512 obrázek za pár vteřin.
    /// </summary>
    public static readonly RecommendedModel DreamShaper8_Sd15 = new(
        Id:                       "dreamshaper-8-sd15",
        Name:                     "DreamShaper 8 (SD 1.5)",
        Description:              "Lehký obrázkový model pro slabší železo — 512×512 za pár vteřin i na 4 GB VRAM.",
        Kind:                     RecommendedModelKind.Image,
        SizeBytes:                2_130_000_000L,
        DownloadUrl:              "https://huggingface.co/Lykon/DreamShaper/resolve/main/DreamShaper_8_pruned.safetensors",
        FileName:                 "DreamShaper_8_pruned.safetensors",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    // ── SDXL tier (rozšíření katalogu — fáze 2 chat → image recommender) ─────

    /// <summary>
    /// Stable Diffusion XL Base 1.0 — oficiální SDXL od Stability AI.
    /// Univerzální realistický model, hostovaný přímo Stability AI na HF.
    /// 6.9 GB, public (žádný token nutný), 1024×1024 nativně.
    /// Lepší fotorealismus než SD 1.5 DreamShaper.
    /// </summary>
    public static readonly RecommendedModel SdxlBase10 = new(
        Id:                       "sdxl-base-1.0",
        Name:                     "Stable Diffusion XL Base 1.0",
        Description:              "Oficiální SDXL pro fotorealistické scény a portréty. 1024×1024 nativně, výrazně lepší než SD 1.5.",
        Kind:                     RecommendedModelKind.Image,
        SizeBytes:                6_938_000_000L,
        DownloadUrl:              "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/sd_xl_base_1.0.safetensors",
        FileName:                 "sd_xl_base_1.0.safetensors",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    /// <summary>
    /// DreamShaper XL Lightning — SDXL fine-tune od Lykon, optimalizovaný pro
    /// stylizovaný digital paint / cinematic look. Lightning varianta běží
    /// na ~4-8 krocích (vs 25 standard SDXL), tedy rychlá.
    /// 6.7 GB, public, hostovaná na HF.
    /// </summary>
    public static readonly RecommendedModel DreamShaperXl_Lightning = new(
        Id:                       "dreamshaper-xl-lightning",
        Name:                     "DreamShaper XL Lightning",
        Description:              "Stylizovaný SDXL — cinematic, fantasy, digital paint. Lightning = 4-8 kroků (rychlejší než klasický SDXL).",
        Kind:                     RecommendedModelKind.Image,
        SizeBytes:                6_700_000_000L,
        DownloadUrl:              "https://huggingface.co/Lykon/dreamshaper-xl-lightning/resolve/main/DreamShaperXL_Lightning.safetensors",
        FileName:                 "DreamShaperXL_Lightning.safetensors",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    /// <summary>
    /// Animagine XL 3.1 — SDXL fine-tune zaměřený na anime / manga / ilustrace.
    /// Od cagliostrolab, hostovaný na HF (public). 6.9 GB.
    /// Pro Anime kind nejlepší public alternativa k Pony (které je jen na Civitai).
    /// </summary>
    public static readonly RecommendedModel AnimagineXl31 = new(
        Id:                       "animagine-xl-3.1",
        Name:                     "Animagine XL 3.1",
        Description:              "SDXL pro anime, manga, ilustrace — vyladěný na 8M+ anime obrázků. Lepší výsledky než SDXL Base pro anime styl.",
        Kind:                     RecommendedModelKind.Image,
        SizeBytes:                6_938_000_000L,
        DownloadUrl:              "https://huggingface.co/cagliostrolab/animagine-xl-3.1/resolve/main/animagine-xl-3.1.safetensors",
        FileName:                 "animagine-xl-3.1.safetensors",
        Sha256:                   null,
        RequiresHuggingFaceToken: false);

    // ── Veřejné kolekce + výběr podle hardwaru ───────────────────────────────

    /// <summary>Kompletní seznam VŠECH modelů (default + low tier + SDXL) — pro debug / unit tests.</summary>
    public static readonly IReadOnlyList<RecommendedModel> All = new[]
    {
        Llama31_8B_Instruct,
        FluxSchnell_Q4,
        Llama32_3B_Instruct,
        DreamShaper8_Sd15,
        SdxlBase10,
        DreamShaperXl_Lightning,
        AnimagineXl31,
    };

    /// <summary>Default tier — pro 8 GB+ VRAM nebo NVIDIA karty.</summary>
    public static readonly IReadOnlyList<RecommendedModel> DefaultTier = new[]
    {
        Llama31_8B_Instruct,
        FluxSchnell_Q4,
    };

    /// <summary>Low tier — pro &lt; 8 GB VRAM, integrované GPU nebo CPU-only stroje.</summary>
    public static readonly IReadOnlyList<RecommendedModel> LowTier = new[]
    {
        Llama32_3B_Instruct,
        DreamShaper8_Sd15,
    };

    /// <summary>
    /// Vrátí 1 chat + 1 image model vhodný pro detekovanou GPU.
    /// Pravidla:
    ///   • NVIDIA s 8+ GB VRAM → default tier (vysoká kvalita)
    ///   • NVIDIA s &lt; 8 GB VRAM → low tier (rychlost)
    ///   • AMD/Intel/Vulkan → low tier (DirectML je pomalejší, větší modely by uživatele frustrovaly)
    ///   • Apple Silicon → default tier (Metal je velmi efektivní i pro 8B+)
    ///   • Unknown / null → low tier (konzervativní, "to musí jet všude")
    /// </summary>
    public static IReadOnlyList<RecommendedModel> PickForGpu(Gpu? gpu)
    {
        if (gpu is null) return LowTier;

        return gpu.Vendor switch
        {
            // Apple Silicon — Metal je velmi efektivní, default tier i s 8 GB unified memory
            GpuVendor.Apple                                    => DefaultTier,

            // NVIDIA — rozhodneme podle VRAM
            GpuVendor.Nvidia when gpu.VramBytes >= 8L * 1024 * 1024 * 1024 => DefaultTier,
            GpuVendor.Nvidia                                   => LowTier,

            // AMD/Intel přes DirectML — pomalejší pipeline, raději menší modely.
            // Při 12 GB+ VRAM by uživatel mohl chtít default tier, ale generování
            // FLUX přes DirectML trvá 1+ minutu, což není dobré first-impression.
            GpuVendor.Amd or GpuVendor.Intel                   => LowTier,

            // Unknown — bezpečnostní default
            _                                                  => LowTier,
        };
    }

    /// <summary>Najde model podle <see cref="RecommendedModel.Id"/>. Vrací null pokud neexistuje.</summary>
    public static RecommendedModel? FindById(string id) =>
        All.FirstOrDefault(m => m.Id == id);

    // ── Doporučení podle ImageKind (pro chat → image gen recommender) ──────────
    //
    // Pro každý ImageKind máme jeden "ideální" curated model — typicky FLUX
    // Schnell (univerzální vysoká kvalita) nebo DreamShaper (lightweight).
    // Pokud uživatel tento model nemá lokálně, recommender uživateli nabídne
    // upgrade.
    //
    // Mapování je záměrně konzervativní (FLUX Schnell pro většinu) — katalog
    // se rozšíří, jakmile přidáme více modelů (Pony XL pro Anime, SDXL Realistic
    // pro Realistic atd.).

    /// <summary>
    /// Vrátí ideální curated image model pro daný kind po rozšíření katalogu
    /// o SDXL tier. Každý kind má teď specific picky:
    /// <list type="bullet">
    /// <item><c>Realistic</c> → SDXL Base 1.0 (oficiální fotorealistický)</item>
    /// <item><c>Stylized</c> → DreamShaper XL Lightning (cinematic / digital paint)</item>
    /// <item><c>Anime</c> → Animagine XL 3.1 (anime SDXL fine-tune)</item>
    /// <item><c>Abstract</c> → FLUX Schnell (nejkreativnější pro abstraktní scény)</item>
    /// <item><c>Auto</c> → SDXL Base 1.0 (univerzální, lepší poměr kvalita/velikost než FLUX)</item>
    /// </list>
    /// </summary>
    public static RecommendedModel? BestImageForKind(ImageKind kind) => kind switch
    {
        ImageKind.Realistic => SdxlBase10,
        ImageKind.Stylized  => DreamShaperXl_Lightning,
        ImageKind.Anime     => AnimagineXl31,
        ImageKind.Abstract  => FluxSchnell_Q4,
        ImageKind.Auto      => SdxlBase10,
        _                   => null,
    };
}
