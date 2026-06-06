using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Vestavěný (nativní) generátor obrázků — inference přímo v aplikaci, bez ComfyUI a bez
/// externího Python procesu. Cílová implementace wrapuje <c>stable-diffusion.cpp</c>
/// (ggml/GGUF) přes P/Invoke, s multi-backend dispatchem (CPU/CUDA/Vulkan/Metal) stejně
/// jako <see cref="ILlamaService"/> u LLM. Viz <c>docs/native-generator-design.md</c>.
///
/// <para>Abstrakce je záměrně paralelní k ComfyUI cestě (ne sjednocená) — nativní generátor
/// má jiný model (přímá inference, ne workflow JSON). UI se mezi nimi přepíná dle nastavení.</para>
/// </summary>
public interface INativeImageGenerator
{
    /// <summary>Dostupnost a info o nativním backendu. Když není dostupný, UI nabídne ComfyUI.</summary>
    NativeGeneratorStatus Status { get; }

    /// <summary>True když je model načtený a připravený generovat.</summary>
    bool IsModelLoaded { get; }

    /// <summary>
    /// Načte SD/SDXL/FLUX model (GGUF/safetensors) na daný backend. Idempotentní pro stejný
    /// model. Lazy init backendu při prvním volání (jako LlamaService).
    /// </summary>
    Task LoadModelAsync(string modelPath, NativeGenBackend backend, CancellationToken ct = default);

    /// <summary>
    /// Vygeneruje obrázek(y) dle zadání. Model musí být načtený. Progres hlásí 0–100
    /// (kroky samplingu). Vrací cesty k uloženým PNG nebo chybu.
    /// </summary>
    Task<NativeImageResult> GenerateAsync(
        NativeImageRequest request, IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>Uvolní model z paměti/VRAM (např. před LLM tahem, ať se vejdou).</summary>
    Task UnloadAsync();
}
