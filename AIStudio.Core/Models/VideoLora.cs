namespace AIStudio.Core.Models;

/// <summary>Fáze pipeline „video → LoRA dataset“ pro progress reporting v UI.</summary>
public enum VideoLoraStage
{
    /// <summary>Příprava — ověření závislostí, založení working dir.</summary>
    Preparing,
    /// <summary>ffmpeg tahá snímky z videí podle plánovače.</summary>
    ExtractingFrames,
    /// <summary>BLIP/WD14 generuje popisky pro navzorkované snímky.</summary>
    Captioning,
    /// <summary>Analýza hotová — vráceny kandidátní snímky k výběru.</summary>
    Completed,
    /// <summary>Analýza selhala (viz status / log).</summary>
    Failed
}

/// <summary>
/// Volby vzorkování snímků z jednoho videa. Defaulty jsou laděné na person/subject LoRA:
/// ~20 kandidátů na video je dost na výběr 15-30 finálních, ale ne tolik, aby se uživatel
/// utopil v náhledech.
/// </summary>
public sealed record FrameSamplingOptions(
    /// <summary>Kolik kandidátních snímků navzorkovat z jednoho videa (1-200).</summary>
    int FramesPerVideo = 20,
    /// <summary>Podíl začátku videa, který se přeskočí (0-0.49) — úvodní černá/intro.</summary>
    double SkipStartFraction = 0.05,
    /// <summary>Podíl konce videa, který se přeskočí (0-0.49) — outro/titulky.</summary>
    double SkipEndFraction = 0.05);

/// <summary>
/// Jeden navzorkovaný snímek nabídnutý uživateli k výběru do datasetu. Immutable — výběr
/// (zaškrtnutí) drží UI ve vlastním VM, ne tento Core záznam.
/// </summary>
public sealed record CandidateFrame(
    /// <summary>Absolutní cesta k extrahovanému snímku (PNG/JPG ve working dir).</summary>
    string FramePath,
    /// <summary>Zdrojové video, ze kterého snímek pochází.</summary>
    string SourceVideoPath,
    /// <summary>Časová pozice snímku ve zdrojovém videu.</summary>
    TimeSpan Timestamp,
    /// <summary>Auto-popisek (BLIP/WD14). Prázdný = caption se nepodařilo vygenerovat.</summary>
    string Caption = "");

/// <summary>
/// Zadání prvotní analýzy (fáze 1): z těchto videí navzorkuj a popiš snímky.
/// Výsledek (<see cref="CandidateFrame"/>[]) jde do UI, kde uživatel vybere, co chce.
/// </summary>
public sealed record VideoLoraAnalysisRequest(
    /// <summary>Cesty k vstupním videím (1..X).</summary>
    IReadOnlyList<string> VideoPaths,
    /// <summary>Volby vzorkování snímků (per video).</summary>
    FrameSamplingOptions Sampling,
    /// <summary><c>"blip"</c> (fotorealistické, default) nebo <c>"wd14"</c> (anime tagger).</summary>
    string CaptionStyle = "blip",
    /// <summary>
    /// Volitelný referenční obrázek subjektu, který se má „hlídat“. Ve fázi 1 se zatím
    /// nepoužívá (uživatel vybírá ručně); rezervováno pro budoucí face/subject matching.
    /// </summary>
    string? SubjectReferenceImagePath = null);

/// <summary>Průběh analýzy (fáze 1) — emitované přes <see cref="System.IProgress{T}"/>.</summary>
public sealed record VideoLoraProgress(
    VideoLoraStage Stage,
    /// <summary>Hotovo položek v aktuální fázi (snímky extrahované / popsané).</summary>
    int Done,
    /// <summary>Celkem položek v aktuální fázi (0 = neznámé).</summary>
    int Total,
    /// <summary>Krátký status pro UI.</summary>
    string StatusLine);
