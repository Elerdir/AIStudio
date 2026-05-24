using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

/// <summary>
/// Rychlá klasifikace uživatelské zprávy v chatu — chce LLM odpověď, nový
/// obrázek, nebo úpravu posledního obrázku?
///
/// <para>Cíl: čistě synchronní heuristika (žádné LLM volání), aby uživatel
/// neviděl latenci. Když detekce není jistá nebo chce manuální kontrolu,
/// UI nabízí toggle ikonku 🎨 (force image / force chat / auto).</para>
///
/// <para>Implementace: <c>ChatImageIntentDetector</c> v Infrastructure —
/// keyword-based regex s českými i anglickými slovesy a obrazovými substantivy.</para>
/// </summary>
public interface IChatImageIntentDetector
{
    /// <summary>
    /// Klasifikuje zprávu. Pokud <paramref name="lastAssistantHadImage"/> je
    /// true a zpráva vypadá jako follow-up úprava ("udělej to v noci", "změň
    /// barvu"), vrátí <see cref="ChatImageIntent.EditPreviousImage"/>.
    /// </summary>
    ChatImageIntent Detect(string userMessage, bool lastAssistantHadImage);
}
