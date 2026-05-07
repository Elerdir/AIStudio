namespace AIStudio.Core.Models;

/// <summary>
/// Co uživatel chce vidět — vyšší úroveň abstrakce než konkrétní model.
/// Mapper pak přiřadí konkrétní lokální checkpoint.
/// </summary>
public enum ImageKind
{
    /// <summary>Fotorealistické scény, portréty — typicky SDXL realistic checkpoint.</summary>
    Realistic,
    /// <summary>Anime/manga/ilustrace — Pony, Illustrious, NoobAI…</summary>
    Anime,
    /// <summary>Stylizovaný art (digital painting, fantasy, cinematic) — DreamShaper apod.</summary>
    Stylized,
    /// <summary>Abstraktní / kreativní / non-photoreal — FLUX si poradí dobře.</summary>
    Abstract,
    /// <summary>Parser si nebyl jistý — mapper použije fallback (cokoliv stažené).</summary>
    Auto,
}

/// <summary>Tři praktické kategorie poměru stran. Mapuje se na detail enum v UI.</summary>
public enum ImageAspect { Square, Landscape, Portrait }

/// <summary>Hint z prompt parseru — fast/normal/hi-res, mapuje se na konkrétní rozlišení.</summary>
public enum ImageQualityHint { Fast, Normal, HighRes }

/// <summary>
/// Strukturovaný výstup intent parseru. Surový český/anglický popis na vstupu,
/// na výstupu zobecněná intent + rozšířený prompt v angličtině pro diffusion.
/// </summary>
public sealed record ImageIntent(
    ImageKind        Kind,
    ImageAspect      Aspect,
    ImageQualityHint Quality,
    /// <summary>Anglický rozšířený prompt — diffusion modely fungují líp s EN.</summary>
    string           EnglishPrompt,
    /// <summary>Vhodné negative tagy podle kind (realistic má jiné než anime).</summary>
    string           NegativePrompt,
    /// <summary>1-věta vysvětlení proč jsem vybral kind/aspect — pro UI „explain".</summary>
    string           Reasoning);
