namespace AIStudio.Core.Models;

/// <summary>
/// Čistý POCO record pro uložení/načtení zprávy z databáze.
///
/// <para>Obrázkové zprávy (chat → image generation): pokud je <see cref="ImagePath"/>
/// nenulový, jde o assistantskou zprávu, která reprezentuje vygenerovaný obrázek.
/// <see cref="Content"/> může v ten okamžik nést doplňující text (např. použitý
/// model, rozšířený EN prompt, reasoning od intent parseru — to vše je metadata,
/// které se může v UI zobrazit pod obrázkem nebo v tooltipu).</para>
///
/// <para><see cref="ImageReferencePath"/> nese cestu k referenčnímu obrázku, který
/// byl vstupem pro img2img follow-up generování (např. uživatel řekne "udělej to
/// v noci" → vezme se předchozí obrázek a edituje). Slouží jen jako audit trail
/// a pro případné regenerování konverzace ze záznamu.</para>
/// </summary>
public record MessageRecord(
    string   Id,
    string   ConversationId,
    string   Role,         // "user" | "assistant" | "system"
    string   Content,
    DateTime Timestamp,
    int      OrderIndex,
    string?  ImagePath          = null,
    string?  ImageReferencePath = null);
