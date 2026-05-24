using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Vysoký orchestrátor pro chat → image generation flow. Vstup je volný česky
/// zadaný popis, výstup je hotový soubor na disku + záznam v galerii.
///
/// <para>Pipeline:</para>
/// <list type="number">
/// <item>Parse intent (<see cref="IImageIntentParser"/>) — czechPrompt →
///   strukturovaný <see cref="ImageIntent"/> (EN prompt, kind, aspect, …).</item>
/// <item>Match model (<see cref="IImageModelMatcher"/>) — vybere konkrétní
///   stažený checkpoint dle kind.</item>
/// <item>Postaví Comfy workflow (txt2img nebo img2img dle reference).</item>
/// <item>Queue + wait pro výsledek z ComfyUI.</item>
/// <item>Stáhne obrázek do <c>%AppData%/AIStudio/Images/</c>.</item>
/// <item>Uloží do galerie (<see cref="IImageRepository"/>) jako kdyby byl
///   vygenerovaný v Image Studiu — uživatel ho najde i tam.</item>
/// </list>
///
/// <para>Nikdy nehodí výjimku — všechno se převede na <see cref="ChatImageGenerationResult"/>
/// s <c>Success=false</c>. UI to zobrazí jako chybu v bublině.</para>
/// </summary>
public interface IChatImageOrchestrator
{
    /// <summary>
    /// Vygeneruje obrázek z volného popisu.
    /// </summary>
    /// <param name="czechPrompt">Surový popis od uživatele (česky / anglicky / mix).</param>
    /// <param name="referenceImagePath">
    /// Pokud zadáno, použije se jako img2img reference (např. follow-up úprava
    /// předchozího obrázku v chatu). null = txt2img od nuly.
    /// </param>
    /// <param name="progress">Progress 0–100 pro UI.</param>
    /// <param name="ct">Cancellation pro Stop tlačítko v chatu.</param>
    Task<ChatImageGenerationResult> GenerateAsync(
        string             czechPrompt,
        string?            referenceImagePath,
        IProgress<int>?    progress,
        CancellationToken  ct);
}
