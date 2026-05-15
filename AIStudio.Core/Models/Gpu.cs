namespace AIStudio.Core.Models;

/// <summary>
/// Výrobce GPU. Detekujeme přes PCI vendor ID nebo název adapteru.
/// Vendor určuje, jaký LLM backend a jakou ComfyUI variantu nainstalovat.
/// </summary>
public enum GpuVendor
{
    /// <summary>Není dostupná žádná GPU (jen integrovaná bez ML akcelerace nebo headless).</summary>
    Unknown = 0,
    /// <summary>NVIDIA — CUDA + nvidia-smi monitoring. Plná podpora.</summary>
    Nvidia,
    /// <summary>AMD — Vulkan pro LLM, DirectML pro ComfyUI na Windows.</summary>
    Amd,
    /// <summary>Intel — Vulkan / DirectML (Arc karty, Iris Xe iGPU). Limitovaná podpora.</summary>
    Intel,
    /// <summary>Apple Silicon (M1/M2/M3/M4) — Metal. Pouze macOS.</summary>
    Apple,
}

/// <summary>
/// ML akcelerační backend, který bude AI Studio používat pro LLM inference.
/// Nemusí 1:1 odpovídat <see cref="GpuVendor"/> — uživatel může vynutit CPU
/// pro debugging, nebo NVIDIA karta může spadnout na CPU pokud chybí CUDA runtime.
/// </summary>
public enum GpuBackend
{
    /// <summary>Žádná GPU akcelerace, čistý CPU. Vždy dostupné, ale pomalé.</summary>
    Cpu = 0,
    /// <summary>NVIDIA CUDA. Vyžaduje CUDA Toolkit (typicky bundled v LlamaSharp Backend).</summary>
    Cuda,
    /// <summary>Vulkan — cross-vendor, funguje na NVIDIA/AMD/Intel.</summary>
    Vulkan,
    /// <summary>Apple Metal. Pouze macOS Apple Silicon.</summary>
    Metal,
    /// <summary>Microsoft DirectML — Windows-only, cross-vendor. Pro ComfyUI image gen.</summary>
    DirectMl,
}

/// <summary>
/// Detekovaná GPU + doporučený backend. Vytváří <see cref="Interfaces.IGpuDetector"/>.
///
/// <see cref="VramBytes"/> je total VRAM v bytech; 0 = nepodařilo se zjistit.
/// Pro NVIDIA karty máme přesnou hodnotu z nvidia-smi, pro AMD/Intel jen
/// WMI AdapterRAM (na Windows 32-bit overflow pro &gt;4 GB karty — tam vrátíme 0).
/// </summary>
/// <param name="Vendor">Detekovaný výrobce.</param>
/// <param name="Name">Lidsky čitelný název ("NVIDIA GeForce RTX 4070", "AMD Radeon RX 6750 XT").</param>
/// <param name="VramBytes">Total VRAM v bytech, 0 = neznámé.</param>
/// <param name="Backend">Doporučený LLM backend. Výchozí volba pro <c>LlamaService</c>.</param>
public sealed record Gpu(
    GpuVendor   Vendor,
    string      Name,
    long        VramBytes,
    GpuBackend  Backend)
{
    /// <summary>Konvenient property: VRAM v gigabajtech (zaokrouhleno na 1 desetinné).</summary>
    public double VramGb => Math.Round(VramBytes / 1_073_741_824.0, 1);

    /// <summary>True pokud máme funkční GPU s ne-CPU backendem.</summary>
    public bool HasGpuAcceleration => Backend != GpuBackend.Cpu;
}
