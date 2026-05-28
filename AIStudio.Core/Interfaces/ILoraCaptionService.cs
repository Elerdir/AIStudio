namespace AIStudio.Core.Interfaces;

/// <summary>
/// Auto-captioning obrázků pro LoRA dataset. Generuje krátké anglické popisky
/// (např. „a woman in a red dress sitting on a beach") přes BLIP model nebo
/// WD14 tagger pro anime.
///
/// <para>Backend volá Python script z sd-scripts repa (clone uvnitř
/// <see cref="ILoraTrainerDependencyService"/>) — žádný extra model download
/// kromě BLIP weights které sd-scripts auto-stáhne při prvním spuštění
/// (~990 MB do HuggingFace cache, pak rychlé).</para>
/// </summary>
public interface ILoraCaptionService
{
    /// <summary>True když právě běží captioning (jeden batch najednou).</summary>
    bool IsCaptioning { get; }

    /// <summary>
    /// Vygeneruje captiony pro všechny obrázky. Volaná z UI „Vygenerovat popisky".
    ///
    /// <para>Vrací dictionary <c>imagePath → caption</c>. Pokud caption pro
    /// konkrétní obrázek selhal, není v dictionary; caller si může nechat
    /// existující ručně napsaný popisek.</para>
    /// </summary>
    /// <param name="style">
    /// <c>"blip"</c> = BLIP-large pro fotorealistické (default),
    /// <c>"wd14"</c> = Waifu Diffusion 1.4 tagger pro anime.
    /// </param>
    Task<IReadOnlyDictionary<string, string>> CaptionAsync(
        IReadOnlyList<string>       imagePaths,
        string                      style    = "blip",
        IProgress<CaptionProgress>? progress = null,
        CancellationToken           ct       = default);
}

/// <summary>Průběh captioningu pro UI status. Emitované jednou per obrázek.</summary>
public sealed record CaptionProgress(
    int    Done,
    int    Total,
    string CurrentImageName,
    string CurrentCaption);
