using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Generuje video přes Wan 2.1 (ComfyUI). Sestaví workflow, zařadí do ComfyUI, počká na
/// výsledek (MP4), uloží na disk a zapíše do galerie jako <c>MediaType=video</c>.
/// </summary>
public interface IVideoGenerationService
{
    Task<VideoGenerationResult> GenerateAsync(
        VideoGenerationRequest request,
        IProgress<int>?        progress = null,
        CancellationToken      ct       = default);

    /// <summary>
    /// Vygeneruje <b>dlouhé video</b> řetězením ~5s Wan segmentů (každý další image→video
    /// z posledního snímku předchozího), spojí je do jednoho MP4 a uloží do galerie. Jednotlivé
    /// segmenty zůstanou jako záloha na disku. Progres hlásí segment k/N + procenta uvnitř.
    /// </summary>
    Task<VideoGenerationResult> GenerateLongVideoAsync(
        LongVideoRequest          request,
        IProgress<LongVideoProgress>? progress = null,
        CancellationToken         ct       = default);
}
