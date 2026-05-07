using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Heuristická implementace matcheru — substring-match na filename.
/// Pokud uživatel pojmenoval modely podivně, fallback dorazí cokoliv.
///
/// Mapování (priorita = první match v daném pořadí):
///   • Realistic → epicrealism, juggernaut, realistic, dreamshaper, photo, real
///   • Anime     → pony, illustrious, noobai, anime, manga, waifu
///   • Stylized  → dreamshaper, stylized, art, fantasy
///   • Abstract  → flux (často umí abstraktní lépe)
///   • Auto      → preferuje SDXL realistic, fallback na cokoliv
/// </summary>
public sealed class ImageModelMatcher : IImageModelMatcher
{
    public string? Match(ImageKind kind, IReadOnlyList<string> availableModels)
    {
        if (availableModels.Count == 0) return null;

        var rules = kind switch
        {
            ImageKind.Realistic => new[] { "epicrealism", "epic", "juggernaut", "realistic", "realisti",
                                            "dreamshaper", "photo", "real" },
            ImageKind.Anime     => new[] { "pony", "illustrious", "illustriousxl",
                                            "noobai", "noob", "anime", "manga", "waifu" },
            ImageKind.Stylized  => new[] { "dreamshaper", "stylized", "fantasy", "art",
                                            "cinematic" },
            ImageKind.Abstract  => new[] { "flux" },
            // Auto = bias na realistic SDXL, ale když nic, vezmi cokoliv
            ImageKind.Auto      => new[] { "epicrealism", "juggernaut", "realistic",
                                            "dreamshaper", "epic" },
            _ => Array.Empty<string>(),
        };

        foreach (var rule in rules)
        {
            var match = availableModels.FirstOrDefault(m =>
                m.Contains(rule, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                Log.Information("ImageModelMatcher: kind={Kind} → '{Match}' (rule '{Rule}')",
                    kind, match, rule);
                return match;
            }
        }

        // Žádný heuristický match — preferuj .safetensors (typicky kvalitnější
        // SDXL) před .gguf. FLUX deps (clip_l, t5xxl, ae) odfiltruj — to jsou
        // pomocné soubory, ne checkpointy pro generování.
        var fallback = availableModels
            .Where(m => !IsFluxDep(m))
            .OrderByDescending(m => m.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? availableModels[0];

        Log.Information("ImageModelMatcher: kind={Kind} → fallback '{Fallback}'", kind, fallback);
        return fallback;
    }

    /// <summary>
    /// FLUX deps (clip_l, t5xxl, ae) občas projdou jako checkpointy v dropdownu.
    /// Pro intent matching je nechceme — vyber jen reálné modely.
    /// </summary>
    private static bool IsFluxDep(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("clip_l")
            || lower.StartsWith("t5xxl")
            || lower.StartsWith("ae.")
            || lower == "ae.safetensors";
    }
}
