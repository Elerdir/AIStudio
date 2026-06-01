namespace AIStudio.Core.Models;

/// <summary>
/// Jeden tréninkový obrázek s popisem. Při tréninku sd-scripts očekává
/// dvojici <c>image.jpg</c> + <c>image.txt</c> (caption) ve stejné složce.
/// AI Studio si tyto soubory dočasně připraví v dataset working dir
/// a po dokončení uklidí — uživatelovy originály zůstávají netknuté.
/// </summary>
public sealed record LoraTrainingImage(
    /// <summary>Absolutní cesta ke zdrojovému obrázku.</summary>
    string ImagePath,
    /// <summary>Textový popis pro trénink. Prázdný = sd-scripts použije název souboru.</summary>
    string Caption);

/// <summary>
/// Parametry tréninku LoRA. Defaulty jsou nastavené pro SDXL na 8-12 GB VRAM
/// kartě s rozumným kompromisem mezi rychlostí a kvalitou. Pro slabší HW
/// jde rank a batch size dolů; pro silnější (16+ GB) nahoru.
/// </summary>
public sealed record LoraTrainingParameters
{
    /// <summary>
    /// Dimenze LoRA matic. 8-128. Vyšší = víc kapacity (lepší detail) ale
    /// větší výstupní soubor a víc VRAM. 32 je standard pro SDXL portrét.
    /// </summary>
    public int Rank { get; init; } = 32;

    /// <summary>
    /// Alpha škálování (typicky Rank/2 nebo Rank). Vyšší = větší vliv LoRA na
    /// finální obrázek. Při Rank=32 je Alpha=16 vyvážený default.
    /// </summary>
    public int Alpha { get; init; } = 16;

    /// <summary>
    /// Celkový počet training stepů. Pro 20-30 fotek typu „person LoRA" je
    /// 1000-1500 sweet spot. Víc = overfit, méně = nedotrénované.
    /// </summary>
    public int Steps { get; init; } = 1500;

    /// <summary>
    /// Learning rate U-Netu. Standardně 1e-4 pro SDXL LoRA. Pro postavy lze
    /// jít na 5e-5 (pomalejší ale stabilnější), pro styly 2e-4.
    /// </summary>
    public double LearningRate { get; init; } = 1e-4;

    /// <summary>
    /// Batch size. 1 = nejméně VRAM, ale pomalé. 2 = lepší gradient pro 12 GB+.
    /// Při OOM padáme na 1 automaticky.
    /// </summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>
    /// Trénovací rozlišení v pixelech. SDXL = 1024, SD 1.5 = 512.
    /// </summary>
    public int Resolution { get; init; } = 1024;

    /// <summary>
    /// FP16 mixed precision — výrazně šetří VRAM bez ztráty kvality.
    /// Default true; vypnout jen na CPU buildu (CPU FP16 je pomalé).
    /// </summary>
    public bool MixedPrecisionFp16 { get; init; } = true;

    /// <summary>
    /// Gradient checkpointing — šetří VRAM tím, že znovu počítá aktivace
    /// při backwardu místo jejich držení v paměti. ~20 % pomalejší ale
    /// na 8 GB VRAM nezbytné pro SDXL.
    /// </summary>
    public bool GradientCheckpointing { get; init; } = true;

    /// <summary>
    /// Optimizer. <c>AdamW8bit</c> (default, bitsandbytes) = 8-bit verze pro
    /// úsporu VRAM. <c>AdamW</c> = klasický FP32, žere více paměti.
    /// </summary>
    public string Optimizer { get; init; } = "AdamW8bit";

    /// <summary>
    /// Po kolika krocích se LoRA průběžně uloží (kvůli kontinuitě, kdyby
    /// trénink spadl). 0 = uložit jen finální výstup.
    /// </summary>
    public int SaveEverySteps { get; init; } = 500;

    /// <summary>
    /// FLUX-only: kolik transformer bloků offloadovat do CPU RAM (--blocks_to_swap).
    /// Šetří VRAM za cenu rychlosti (každý krok přesouvá bloky GPU↔RAM). 0 = vypnuto
    /// (vejde se díky gradient_checkpointing + fp8_base). VM nastavuje adaptivně dle
    /// VRAM: 24 GB → 0 (rychlé), 16 GB → 8, 12 GB → 16, méně → 24. Ignorováno pro SD/SDXL.
    /// </summary>
    public int BlocksToSwap { get; init; } = 0;

    /// <summary>
    /// Defaults laděné podle základního modelu — SDXL má jiné optimum
    /// než SD 1.5 (resolution, learning rate, doporučené stepy).
    /// </summary>
    public static LoraTrainingParameters DefaultsFor(string baseModelFileName)
    {
        var lower = baseModelFileName.ToLowerInvariant();
        var isSdxl = lower.Contains("xl") || lower.Contains("sdxl");
        var isFlux = lower.Contains("flux");

        if (isFlux)
            return new LoraTrainingParameters
            {
                Rank        = 16,           // FLUX LoRA je citlivá na rank — 16 je sweet spot
                Alpha       = 16,
                Steps       = 2000,
                LearningRate= 1e-4,
                Resolution  = 1024,
            };

        if (isSdxl)
            return new LoraTrainingParameters
            {
                Rank        = 32,
                Alpha       = 16,
                Steps       = 1500,
                Resolution  = 1024,
            };

        // SD 1.5 a kompatibilní
        return new LoraTrainingParameters
        {
            Rank        = 32,
            Alpha       = 16,
            Steps       = 1500,
            LearningRate= 1e-4,
            Resolution  = 512,
        };
    }
}

/// <summary>
/// Kompletní zadání pro LoRA trénink. Pasuje se do
/// <see cref="Interfaces.ILoraTrainerService.TrainAsync"/>.
/// </summary>
public sealed record LoraTrainingRequest(
    /// <summary>Název výstupního souboru bez přípony (např. „Nikol_v1").</summary>
    string Name,
    /// <summary>Absolutní cesta k základnímu modelu (.safetensors), na kterém se trénuje.</summary>
    string BaseModelPath,
    /// <summary>15-30 obrázků s popisky. Méně = nedotrénovaný, víc = pomalejší.</summary>
    IReadOnlyList<LoraTrainingImage> Dataset,
    /// <summary>Parametry tréninku. Použij <see cref="LoraTrainingParameters.DefaultsFor"/>.</summary>
    LoraTrainingParameters Parameters,
    /// <summary>Cílový adresář pro <c>{Name}.safetensors</c> — typicky <c>Models/loras/</c>.</summary>
    string OutputDirectory,
    // ── FLUX-only závislosti (null pro SD/SDXL) ──────────────────────────────
    // FLUX trénink (flux_train_network.py) vyžaduje text encodery + VAE jako
    // SAMOSTATNÉ soubory (na rozdíl od SD/SDXL, kde jsou v checkpointu). VM je
    // resolvne z Models složky (sdílí je s FLUX generováním přes FluxDependencyService).
    /// <summary>FLUX: cesta ke clip_l.safetensors. Null = ne-FLUX trénink.</summary>
    string? FluxClipLPath = null,
    /// <summary>FLUX: cesta k t5xxl encoderu.</summary>
    string? FluxT5Path = null,
    /// <summary>FLUX: cesta k ae.safetensors (VAE).</summary>
    string? FluxAePath = null,
    // ── Režim popisků ────────────────────────────────────────────────────────
    /// <summary>
    /// True = popisky všech fotek se při tréninku přepíšou na samotný trigger token
    /// (sanitizovaný <see cref="Name"/>). Doporučeno pro LoRA na konkrétní OSOBU /
    /// POSTAVU / OBLIČEJ: model pak váže identitu na trigger, ne na popisná slova.
    /// Když je false, použijí se popisky z datasetu (vhodné pro styl/koncept LoRA).
    /// </summary>
    bool TokenOnlyCaptions = false);

/// <summary>
/// Průběh tréninku — emitované přes <see cref="IProgress{T}"/> typicky 1× za step.
/// UI z toho staví progress bar + loss graf + ETA.
/// </summary>
public sealed record LoraTrainingProgress(
    /// <summary>Aktuální krok (1-based, 0 = před prvním krokem / inicializace).</summary>
    int CurrentStep,
    /// <summary>Celkový počet stepů z requestu.</summary>
    int TotalSteps,
    /// <summary>Loss z posledního kroku — null pokud ještě nedorazil.</summary>
    double? CurrentLoss,
    /// <summary>Čas od startu tréninku.</summary>
    TimeSpan Elapsed,
    /// <summary>Odhad zbývajícího času podle průměrné rychlosti per-step.</summary>
    TimeSpan? EstimatedRemaining,
    /// <summary>Krátký textový status pro UI („Načítám checkpoint…", „Krok 234/1500", …).</summary>
    string StatusLine);

/// <summary>Výsledek tréninku — co se vrátí z <c>TrainAsync</c>.</summary>
public sealed record LoraTrainingResult(
    bool       Success,
    string?    OutputFilePath,
    string?    ErrorMessage,
    TimeSpan   TotalTime);

/// <summary>
/// Progress při instalaci pip závislostí + sd-scripts. Stejný pattern jako
/// <see cref="ComfyInstallProgress"/>: jeden objekt s aktuálním stage + bytes.
/// </summary>
public sealed record LoraTrainerInstallProgress(
    /// <summary>„Stahuji pip balíky", „Klonuji sd-scripts", „Hotovo" atd.</summary>
    string Stage,
    /// <summary>0-100 nebo null pokud nedefinováno (např. pip install nemá progress).</summary>
    int?   PercentComplete,
    /// <summary>Pro download stage — kolik MB už staženo.</summary>
    long?  DownloadedBytes  = null,
    long?  TotalBytes       = null);
