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
}
