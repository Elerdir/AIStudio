using AIStudio.Core.Services;

namespace AIStudio.Core.Models;

/// <summary>
/// Zadání pro generování videa přes Wan 2.1. <see cref="StartImagePath"/> je povinná
/// pro obrázek→video modely (<see cref="WanVideoMode.ImageToVideo"/>) a ignorovaná pro
/// text→video.
/// </summary>
public sealed record VideoGenerationRequest(
    WanVideoModel Model,
    string        Prompt,
    int           Width,
    int           Height,
    int           Length,      // počet snímků (např. 33 ≈ 2 s při 16 FPS)
    int           Steps,
    double        Cfg,
    long          Seed,
    string?       StartImagePath = null,
    int           Fps = 16,
    string?       NegativePrompt = null,
    IReadOnlyList<LoraItem>? Loras = null,
    bool          Upscale = false,        // post-proces 2× ESRGAN (nad 720p)
    string?       UpscaleModel = null);   // soubor v upscale_models/ (RealESRGAN_x4plus.pth)

/// <summary>
/// Zadání pro <b>dlouhé video</b> skládané z řetězených ~5s Wan segmentů. Segment 1 je
/// text→video (z <see cref="Prompt"/>) nebo image→video (z <see cref="StartImagePath"/>),
/// každý další je image→video z posledního snímku předchozího segmentu. Rozplánování řeší
/// <see cref="Services.VideoSegmentPlanner"/> podle <see cref="TargetSeconds"/> a <see cref="Fps"/>.
/// </summary>
public sealed record LongVideoRequest(
    WanVideoModel I2VModel,            // model pro navazující (a image-start) segmenty
    WanVideoModel? T2VModel,           // model pro text-start 1. segment (null = start z obrázku)
    string         Prompt,
    int            Width,
    int            Height,
    int            TargetSeconds,
    int            Fps,
    int            Steps,
    double         Cfg,
    long           Seed,
    string?        StartImagePath = null,
    string?        NegativePrompt = null,
    IReadOnlyList<LoraItem>? Loras = null,
    bool           Upscale = false,
    string?        UpscaleModel = null);

/// <summary>Průběh generování dlouhého videa — segment k/N + procenta uvnitř segmentu.</summary>
public sealed record LongVideoProgress(
    int    OverallPercent,
    int    SegmentIndex,    // 1-based
    int    SegmentCount,
    string Stage);          // např. „Segment 2/6 — generuji"

/// <summary>
/// Výsledek generování videa. Když chybí závislosti (velké modely nejsou stažené),
/// <see cref="MissingDependencies"/> je neprázdný a UI má nabídnout jejich stažení.
/// </summary>
public sealed record VideoGenerationResult(
    bool                   Success,
    string?                FilePath,
    string?                ErrorMessage,
    IReadOnlyList<string>? MissingDependencies = null);
