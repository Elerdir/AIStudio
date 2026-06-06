namespace AIStudio.Core.Models;

/// <summary>
/// Výpočetní backend vlastního (vestavěného) generátoru obrázků. Mirror logiky LLM
/// backendů (<c>GpuBackend</c>) — výběr dle <c>IGpuDetector</c>, lazy init při prvním
/// načtení modelu.
/// </summary>
public enum NativeGenBackend
{
    Cpu,
    Cuda,
    Vulkan,
    Metal,
}

/// <summary>
/// Rodina SD modelu — určuje výchozí parametry (rozlišení, CFG, sampler) a kompatibilitu.
/// Detekce z tensorů/metadat (rozšíří <c>SafetensorsInspector</c>), ne z názvu souboru.
/// </summary>
public enum NativeModelFamily
{
    Unknown,
    Sd1,    // Stable Diffusion 1.x (512²)
    Sd2,    // Stable Diffusion 2.x (768²)
    Sdxl,   // SDXL (1024²)
    Sd3,    // Stable Diffusion 3
    Flux,   // FLUX.1 (schnell/dev)
}

/// <summary>Jeden LoRA adaptér pro vestavěný generátor.</summary>
public sealed record NativeLora(string Path, double Scale = 1.0);

/// <summary>
/// Zadání pro vestavěný txt2img/img2img. Bez vazby na ComfyUI workflow — přímý vstup do
/// nativní inference. <see cref="InitImagePath"/> != null = img2img (s <see cref="Denoise"/>).
/// </summary>
public sealed record NativeImageRequest(
    string  ModelPath,
    string  Prompt,
    string  NegativePrompt,
    int     Width,
    int     Height,
    int     Steps,
    double  CfgScale,
    long    Seed,
    string  SamplerName,                 // AI Studio název; mapuje NativeSamplerMap
    int     BatchCount   = 1,
    string? VaePath      = null,
    IReadOnlyList<NativeLora>? Loras = null,
    string? InitImagePath = null,        // img2img vstup (null = txt2img)
    double  Denoise       = 0.75);       // jen img2img

/// <summary>Výsledek vestavěné generace — cesty k uloženým PNG nebo chyba.</summary>
public sealed record NativeImageResult(
    bool                   Success,
    IReadOnlyList<string>  FilePaths,
    string?                ErrorMessage = null);

/// <summary>
/// Stav vestavěného generátoru — jestli je nativní backend dostupný a jaký. Když není
/// (chybí nativní lib / nepodporovaná platforma), <see cref="IsAvailable"/> == false a UI
/// nabídne fallback na ComfyUI.
/// </summary>
public sealed record NativeGeneratorStatus(
    bool             IsAvailable,
    NativeGenBackend Backend,
    string           BackendInfo,
    string?          UnavailableReason = null);
