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
    string?       NegativePrompt = null);

/// <summary>
/// Výsledek generování videa. Když chybí závislosti (velké modely nejsou stažené),
/// <see cref="MissingDependencies"/> je neprázdný a UI má nabídnout jejich stažení.
/// </summary>
public sealed record VideoGenerationResult(
    bool                   Success,
    string?                FilePath,
    string?                ErrorMessage,
    IReadOnlyList<string>? MissingDependencies = null);
