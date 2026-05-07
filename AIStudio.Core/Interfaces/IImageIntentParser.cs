using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Parsuje surový popis obrázku (česky / anglicky) na strukturovaný
/// <see cref="ImageIntent"/>. Implementace volá lokální LLM se strukturovaným
/// system promptem; když LLM není načtený / selže, vrací fallback intent
/// s rozumnými defaulty (kind=Auto, aspect=Square, prompt=raw).
/// </summary>
public interface IImageIntentParser
{
    /// <summary>
    /// Z volného popisu vyrobí intent. Nikdy nehází — selhání se sklouzne
    /// do fallback intent s reasoning="Fallback: …".
    /// </summary>
    Task<ImageIntent> ParseAsync(string rawPrompt, CancellationToken ct = default);
}
