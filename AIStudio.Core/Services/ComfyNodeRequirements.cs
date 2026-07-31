namespace AIStudio.Core.Services;

/// <summary>
/// Skupina uzlů, které dohromady drží jednu funkci aplikace. Když z ní kterýkoliv
/// uzel chybí, ta funkce v ComfyUI spadne — typicky až ve chvíli, kdy uživatel
/// zmáčkne Generovat.
/// </summary>
/// <param name="Feature">Lidský název funkce (do logu a do UI).</param>
/// <param name="Nodes"><c>class_type</c> hodnoty, které pro ni workflow builder posílá.</param>
/// <param name="CustomNodePack">
/// Balík custom nodů, který uzly dodává (null = součást základního ComfyUI).
/// Chybějící core uzel = ComfyUI je moc staré; chybějící custom uzel = neproběhla
/// (nebo selhala) auto-instalace balíku.
/// </param>
public sealed record ComfyNodeGroup(
    string                Feature,
    IReadOnlyList<string> Nodes,
    string?               CustomNodePack = null);

/// <summary>Výsledek kontroly jedné skupiny — co konkrétně chybí.</summary>
public sealed record ComfyMissingNodes(
    string                Feature,
    IReadOnlyList<string> Missing,
    string?               CustomNodePack);

/// <summary>
/// Výsledek celé kontroly. <paramref name="Completed"/> odlišuje „ověřeno, nic
/// nechybí" od „nepodařilo se zjistit" (ComfyUI neběží / neodpovědělo) — v obou
/// případech je <paramref name="Missing"/> prázdné, ale znamenají opak.
/// </summary>
public sealed record ComfyNodeCheckResult(
    bool                             Completed,
    int                              AvailableCount,
    IReadOnlyList<ComfyMissingNodes> Missing)
{
    /// <summary>Kontrola proběhla a všechny sledované uzly jsou k dispozici.</summary>
    public bool AllPresent => Completed && Missing.Count == 0;

    /// <summary>Výsledek pro případ, kdy se stav nepodařilo zjistit.</summary>
    public static ComfyNodeCheckResult NotAvailable { get; } =
        new(false, 0, Array.Empty<ComfyMissingNodes>());
}

/// <summary>
/// Uzly, na kterých stojí jednotlivé funkce — a kontrola proti tomu, co běžící
/// ComfyUI skutečně nabízí (<c>/object_info</c>).
///
/// <para><b>Proč to existuje:</b> workflow buildery odkazují uzly řetězcem
/// (<c>"RIFE VFI"</c>, <c>"VHS_LoadVideoPath"</c>, …). Překlep, přejmenovaný uzel
/// po update ComfyUI nebo tiše neproběhlá instalace custom balíku se jinak projeví
/// až chybou z generování — po minutách čekání a nad plnou VRAM. Unit testy tuhle
/// třídu chyb nechytí: ověřují tvar JSON, ne to, že uzel na druhé straně existuje.
/// Tohle je jediné HTTP volání, které rozdíl odhalí hned po startu.</para>
///
/// <para>Seznamy odpovídají <see cref="ComfyWorkflowBuilder"/>. Když tam přibude
/// uzel, patří i sem — jinak kontrola mlčí o něčem, co může chybět.</para>
/// </summary>
public static class ComfyNodeRequirements
{
    /// <summary>Balík ComfyUI-VideoHelperSuite (video I/O — načtení i složení MP4).</summary>
    public const string PackVideoHelper   = "ComfyUI-VideoHelperSuite";

    /// <summary>Balík ComfyUI-Frame-Interpolation (RIFE).</summary>
    public const string PackFrameInterp   = "ComfyUI-Frame-Interpolation";

    /// <summary>Balík ComfyUI-GGUF (kvantované UNET modely).</summary>
    public const string PackGguf          = "ComfyUI-GGUF";

    /// <summary>Balík ComfyUI-Impact-Pack (detekce + dodělání obličejů).</summary>
    public const string PackImpact        = "ComfyUI-Impact-Pack";

    /// <summary>Balík ComfyUI-PuLID-Flux (zachování identity osoby).</summary>
    public const string PackPulid         = "ComfyUI-PuLID-Flux";

    /// <summary>
    /// Všechny sledované skupiny. Pořadí = od nejzákladnějších funkcí k volitelným,
    /// aby log i UI četly odshora podle důležitosti.
    /// </summary>
    public static IReadOnlyList<ComfyNodeGroup> All { get; } = new ComfyNodeGroup[]
    {
        new("Generování obrázků (SD/SDXL)", new[]
        {
            "CheckpointLoaderSimple", "EmptyLatentImage", "CLIPTextEncode",
            "KSampler", "VAEDecode", "SaveImage",
        }),

        new("Img2img a reference", new[]
        {
            "LoadImage", "ImageScale", "VAEEncode", "LatentBlend", "RepeatLatentBatch",
        }),

        new("LoRA", new[] { "LoraLoader", "LoraLoaderModelOnly" }),

        new("FLUX", new[] { "UNETLoader", "DualCLIPLoader", "VAELoader", "FluxGuidance" }),

        new("FLUX GGUF (kvantované modely)", new[] { "UnetLoaderGGUF" }, PackGguf),

        // FluxKontextImageScale + ReferenceLatent jsou novější core uzly — na starším
        // ComfyUI chybí, i když je jinak instalace v pořádku.
        new("FLUX Kontext (editace obrázku)", new[] { "FluxKontextImageScale", "ReferenceLatent" }),

        new("Upscale", new[] { "UpscaleModelLoader", "ImageUpscaleWithModel", "ImageScaleBy", "LatentUpscale" }),

        new("Video (Wan 2.1)", new[]
        {
            "CLIPLoader", "CLIPVisionLoader", "CLIPVisionEncode",
            "WanImageToVideo", "EmptyHunyuanLatentVideo", "SaveAnimatedWEBP",
        }),

        new("Video — výstup MP4", new[] { "VHS_VideoCombine" }, PackVideoHelper),

        // Dlouhé video řetězí segmenty: poslední snímek segmentu (ImageFromBatch)
        // je vstupem dalšího, hotové MP4 se pak znovu načítá (VHS_LoadVideoPath).
        new("Dlouhé video (řetězení segmentů)", new[] { "ImageFromBatch" }),
        new("Dlouhé video — načtení MP4", new[] { "VHS_LoadVideoPath" }, PackVideoHelper),

        // Název uzlu je opravdu s mezerou — snadný cíl pro tichý překlep.
        new("Plynulejší video (RIFE interpolace)", new[] { "RIFE VFI" }, PackFrameInterp),

        new("Dodělání obličejů", new[] { "UltralyticsDetectorProvider", "FaceDetailer" }, PackImpact),

        new("Zachování identity (PuLID)", new[]
        {
            "PulidFluxModelLoader", "PulidFluxEvaClipLoader",
            "PulidFluxInsightFaceLoader", "ApplyPulidFlux",
        }, PackPulid),
    };

    /// <summary>
    /// Porovná požadované uzly s tím, co ComfyUI nabízí. Vrací jen skupiny, kterým
    /// něco chybí — prázdný výsledek = všechno sedí.
    /// </summary>
    /// <param name="availableNodes">
    /// Názvy uzlů z <c>/object_info</c>. Porovnává se přesně (ordinálně), protože
    /// ComfyUI bere <c>class_type</c> case-sensitive — <c>vhs_videocombine</c> by
    /// při generování neprošlo, takže se nesmí počítat jako shoda.
    /// </param>
    public static IReadOnlyList<ComfyMissingNodes> Evaluate(IEnumerable<string> availableNodes)
    {
        ArgumentNullException.ThrowIfNull(availableNodes);

        var available = availableNodes as ISet<string>
                        ?? new HashSet<string>(availableNodes, StringComparer.Ordinal);

        var result = new List<ComfyMissingNodes>();
        foreach (var group in All)
        {
            var missing = group.Nodes.Where(n => !available.Contains(n)).ToArray();
            if (missing.Length > 0)
                result.Add(new ComfyMissingNodes(group.Feature, missing, group.CustomNodePack));
        }
        return result;
    }

    /// <summary>
    /// Jednořádkové shrnutí do logu / statusu. <paramref name="missing"/> je výstup
    /// z <see cref="Evaluate"/>.
    /// </summary>
    public static string Describe(IReadOnlyList<ComfyMissingNodes> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        if (missing.Count == 0) return "Všechny potřebné ComfyUI uzly jsou dostupné.";

        var parts = missing.Select(m =>
        {
            var nodes = string.Join(", ", m.Missing);
            return m.CustomNodePack is null
                ? $"{m.Feature}: chybí {nodes}"
                : $"{m.Feature}: chybí {nodes} (balík {m.CustomNodePack})";
        });
        return string.Join("; ", parts);
    }
}
