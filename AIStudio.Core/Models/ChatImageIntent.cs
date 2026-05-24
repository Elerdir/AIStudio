namespace AIStudio.Core.Models;

/// <summary>
/// Co uživatel asi chce udělat svou zprávou v chatu — výsledek rychlé
/// klasifikace zprávy předtím, než ji pošleme do LLM nebo do generování obrázků.
///
/// <para>Detekce dělá <c>IChatImageIntentDetector</c>; výchozí implementace je
/// keyword-based hybrid (rychlé heuristiky + možnost override z UI).</para>
/// </summary>
public enum ChatImageIntent
{
    /// <summary>Standardní text zpráva — pošli do LLM.</summary>
    Chat,

    /// <summary>Vygeneruj nový obrázek z této zprávy jako promptu.</summary>
    GenerateImage,

    /// <summary>
    /// Uprav předchozí obrázek (img2img follow-up). Vyžaduje, aby předchozí
    /// assistantská zpráva už měla nějaký obrázek — jinak se downgradne na
    /// <see cref="GenerateImage"/>.
    /// </summary>
    EditPreviousImage,
}
