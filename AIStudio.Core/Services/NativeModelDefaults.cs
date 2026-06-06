using AIStudio.Core.Models;

namespace AIStudio.Core.Services;

/// <summary>
/// Výchozí generační parametry podle rodiny SD modelu (rozlišení, kroky, CFG). Čistá funkce
/// — UI / generátor jimi předvyplní hodnoty, když uživatel nezadá vlastní. Hodnoty vychází
/// z běžně doporučovaných nastavení pro danou rodinu.
/// </summary>
public static class NativeModelDefaults
{
    /// <summary>Výchozí (šířka, výška, kroky, CFG) pro danou rodinu modelu.</summary>
    public static (int Width, int Height, int Steps, double Cfg) For(NativeModelFamily family) => family switch
    {
        NativeModelFamily.Sd1  => (512, 512, 20, 7.0),
        NativeModelFamily.Sd2  => (768, 768, 20, 7.0),
        NativeModelFamily.Sdxl => (1024, 1024, 25, 6.0),
        NativeModelFamily.Sd3  => (1024, 1024, 28, 4.5),
        NativeModelFamily.Flux => (1024, 1024, 20, 1.0),   // FLUX je guidance-distilled → CFG ~1
        _                      => (512, 512, 20, 7.0),       // Unknown → bezpečné SD1.5 defaulty
    };
}
