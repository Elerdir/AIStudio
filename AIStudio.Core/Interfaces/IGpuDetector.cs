using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Detekce GPU pro účely doporučení LLM backendu a ComfyUI varianty.
///
/// Implementace per-OS:
///  • <c>WindowsGpuDetector</c> — WMI Win32_VideoController + nvidia-smi probe.
///  • <c>MacOsGpuDetector</c> — <c>system_profiler SPDisplaysDataType</c>
///    (přidá se s macOS portem).
///  • Pokud nic neprojde, vrací <see cref="Gpu"/> s <see cref="GpuVendor.Unknown"/>
///    + <see cref="GpuBackend.Cpu"/>.
///
/// Volání je asynchronní, protože WMI / nvidia-smi mohou trvat 100–500 ms
/// (zejména při prvním cold start v session). Voláme typicky jednou při startu
/// aplikace a cache-ujeme výsledek do <see cref="ISystemMonitorService"/>.
/// </summary>
public interface IGpuDetector
{
    /// <summary>
    /// Detekuje primární GPU. Pokud systém má víc karet (např. iGPU + dGPU),
    /// vrátí dGPU (vyšší VRAM + diskrétní vendor). Pokud žádná diskrétní karta,
    /// vrátí integrovanou. Pokud nic, vrátí Unknown/Cpu.
    /// </summary>
    Task<Gpu> DetectAsync(CancellationToken ct = default);
}
