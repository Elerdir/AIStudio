using AIStudio.Core.Models;

namespace AIStudio.Core.Services;

/// <summary>
/// Pojistka proti záměně „SD model + FLUX parametry". Vestavěný generátor drží výchozí
/// model „FLUX.1 Schnell" → CFG ≈ 1.0 a ~4 kroky (FLUX je guidance-distilled). Když ale
/// reálně vybraný/rozpoznaný checkpoint je SD/SDXL/SD2/SD3, <b>CFG ≈ 1 způsobí, že model
/// prompt prakticky ignoruje</b> (žádná classifier-free guidance) — výsledek je koherentní,
/// ale úplně mimo zadání. Tahle čistá funkce to detekuje a vrátí rozumné parametry rodiny.
///
/// <para>Nezasahuje do <b>rychlých variant</b> (turbo / lightning / lcm / hyper), které na
/// nízké CFF a málo krocích běží <i>záměrně</i>.</para>
/// </summary>
public static class NativeParamGuard
{
    /// <summary>Markery modelů, které legitimně běží na nízké CFG / málo krocích.</summary>
    private static readonly string[] FastVariantMarkers = { "turbo", "lightning", "lcm", "hyper" };

    /// <summary>CFG ≤ tato hodnota se považuje za „FLUX-ish" (bez reálné guidance).</summary>
    public const double FluxishCfgThreshold = 1.5;

    /// <summary>Pod tolik kroků se i počet kroků považuje za FLUX-ish a opraví se.</summary>
    public const int FluxishStepThreshold = 8;

    /// <summary>
    /// Vrátí opravené (Steps, Cfg) pro nativní generaci. <paramref name="corrected"/> je true,
    /// pokud zásah proběhl (volající to zaloguje / ukáže v UI).
    /// </summary>
    public static (int Steps, double Cfg, bool Corrected) Correct(
        NativeModelFamily family, string? modelFileName, int steps, double cfg)
    {
        // FLUX s CFG~1 je správně — nesahat.
        if (family == NativeModelFamily.Flux) return (steps, cfg, false);

        // CFG je rozumné (uživatel nastavil reálnou guidance) — nesahat.
        if (cfg > FluxishCfgThreshold) return (steps, cfg, false);

        // Rychlé varianty (turbo/lightning/lcm/hyper) běží na nízké CFG záměrně — nesahat.
        var name = (modelFileName ?? string.Empty).ToLowerInvariant();
        foreach (var m in FastVariantMarkers)
            if (name.Contains(m)) return (steps, cfg, false);

        var (_, _, defSteps, defCfg) = NativeModelDefaults.For(family);

        // CFG opravíme vždy; kroky jen když jsou taky FLUX-málo (uživatel mohl mít kroky OK).
        var newSteps = steps < FluxishStepThreshold ? defSteps : steps;
        return (newSteps, defCfg, true);
    }
}
