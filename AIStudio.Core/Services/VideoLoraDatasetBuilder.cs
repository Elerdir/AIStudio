using System.Text;
using AIStudio.Core.Models;

namespace AIStudio.Core.Services;

/// <summary>
/// Sestaví LoRA dataset (<see cref="LoraTrainingImage"/>[]) z uživatelem vybraných
/// kandidátních snímků. Čistá funkce → testovatelná.
///
/// <para>Dva režimy popisků (stejná filozofie jako <c>SdScriptsLoraTrainer</c>):</para>
/// <list type="bullet">
/// <item><b>Token-only</b> (osoba/postava/obličej): caption = jen trigger token. Identita
///   se naváže na token, ne na popisná slova. (Trainer to při <c>TokenOnlyCaptions=true</c>
///   stejně vynutí — tady to držíme konzistentní.)</item>
/// <item><b>Popisný</b> (styl/koncept): caption = <c>"trigger, &lt;auto-popisek&gt;"</c> —
///   trigger vepředu, aby se subjekt vázal na něj, zbytek dává kontext.</item>
/// </list>
/// </summary>
public static class VideoLoraDatasetBuilder
{
    /// <summary>
    /// Z vybraných snímků postaví dataset s popisky. <paramref name="triggerToken"/> je
    /// název/identita LoRA (sanitizuje se na bezpečný token).
    /// </summary>
    public static IReadOnlyList<LoraTrainingImage> Build(
        IReadOnlyList<CandidateFrame> selected, string triggerToken, bool tokenOnly)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var trigger = NormalizeTrigger(triggerToken);

        var result = new List<LoraTrainingImage>(selected.Count);
        foreach (var f in selected)
        {
            string caption;
            if (tokenOnly || string.IsNullOrWhiteSpace(f.Caption))
                caption = trigger;
            else
                caption = string.IsNullOrEmpty(trigger)
                    ? f.Caption.Trim()
                    : $"{trigger}, {f.Caption.Trim()}";

            result.Add(new LoraTrainingImage(f.FramePath, caption));
        }
        return result;
    }

    /// <summary>
    /// Sanitizuje název na trigger token bezpečný pro caption i název souboru — shodně
    /// s <c>SdScriptsLoraTrainer.SanitizeForFolderName</c> (alnum/_/- ponecháno, zbytek
    /// na '_', lowercase).
    /// </summary>
    public static string NormalizeTrigger(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString().ToLowerInvariant();
    }
}
