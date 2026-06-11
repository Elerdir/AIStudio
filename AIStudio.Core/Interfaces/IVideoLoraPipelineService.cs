using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Fáze 1 pipeline „video → LoRA“: z dodaných videí navzorkuje snímky (přes
/// <see cref="Services.VideoFrameSamplingPlanner"/>), extrahuje je ffmpegem a popíše
/// (<see cref="ILoraCaptionService"/>). Vrací kandidátní snímky, ze kterých si uživatel
/// v UI vybere, co půjde do datasetu.
///
/// <para><b>Fáze 2 (trénink) se sem zámeřně nepřidává</b> — po výběru uživatele se
/// dataset sestaví čistým <see cref="Services.VideoLoraDatasetBuilder"/> a předá
/// existujícímu <see cref="ILoraTrainerService"/>. Žádná nová tréninková logika.</para>
/// </summary>
public interface IVideoLoraPipelineService
{
    /// <summary>True dokud běží analýza (jeden běh najednou).</summary>
    bool IsAnalyzing { get; }

    /// <summary>
    /// Navzorkuje + popíše snímky ze všech videí v requestu. Snímky se ukládají do
    /// dočasné working dir; volající (po výběru) si vybrané cesty zkopíruje do datasetu
    /// a working dir se uklidí přes <see cref="CleanupAsync"/>.
    /// </summary>
    /// <returns>
    /// Plochý seznam <see cref="CandidateFrame"/> ze všech videí (každý nese své zdrojové
    /// video + timestamp). Nikdy nehází — selhání se reportuje přes progress (stage
    /// <see cref="VideoLoraStage.Failed"/>) a vrátí prázdný/částečný seznam.
    /// </returns>
    Task<IReadOnlyList<CandidateFrame>> AnalyzeAsync(
        VideoLoraAnalysisRequest          request,
        IProgress<VideoLoraProgress>?     progress = null,
        CancellationToken                 ct       = default);

    /// <summary>Smaže working dir s extrahovanými snímky (po sestavení datasetu / zrušení).</summary>
    Task CleanupAsync();
}
