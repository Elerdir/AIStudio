using AIStudio.Core.Models;

namespace AIStudio.Core.Services;

/// <summary>
/// Odhad rodiny SD modelu z názvu souboru (heuristika). Slouží k předvyplnění výchozích
/// parametrů (<see cref="NativeModelDefaults"/>) ve vestavěném generátoru, dokud nemáme
/// přesnou detekci z tensorů (rozšíření <c>SafetensorsInspector</c> — TODO Fáze 2+).
///
/// <para>Čistá funkce, plně testovatelná. Heuristika dle pořadí specifičnosti: FLUX → SD3 →
/// SDXL → SD2 → SD1. Když nic nesedí, vrací <see cref="NativeModelFamily.Unknown"/>.</para>
/// </summary>
public static class NativeModelFamilyDetector
{
    /// <summary>Odhadne rodinu z názvu (nebo cesty) souboru modelu.</summary>
    public static NativeModelFamily GuessFromFileName(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return NativeModelFamily.Unknown;

        // Ořež adresář podle OBOU oddělovačů — Path.GetFileName je platform-závislé (na Unixu
        // nebere '\' jako oddělovač, takže by celá Windows cesta prošla jako název a např.
        // „C:\flux\sd_xl…" by chybně chytla „flux"). Bez tohohle padal test na macOS/Linux.
        var normalized = fileNameOrPath.Replace('\\', '/');
        var slash      = normalized.LastIndexOf('/');
        var n          = (slash >= 0 ? normalized[(slash + 1)..] : normalized).ToLowerInvariant();

        if (n.Contains("flux")) return NativeModelFamily.Flux;

        if (n.Contains("sd3") || n.Contains("sd_3") || n.Contains("stable-diffusion-3") || n.Contains("stable_diffusion_3"))
            return NativeModelFamily.Sd3;

        if (n.Contains("sdxl") || n.Contains("xl") || n.Contains("illustrious") || n.Contains("pony"))
            return NativeModelFamily.Sdxl;

        if (n.Contains("sd2") || n.Contains("sd_2") || n.Contains("v2-") || n.Contains("768"))
            return NativeModelFamily.Sd2;

        if (n.Contains("sd15") || n.Contains("sd1.5") || n.Contains("sd_1") || n.Contains("v1-5") || n.Contains("v1.5"))
            return NativeModelFamily.Sd1;

        return NativeModelFamily.Unknown;
    }
}
