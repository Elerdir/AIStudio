using AIStudio.Core.Models;

namespace AIStudio.Core.Interfaces;

public interface IChatRepository
{
    /// <summary>Vytvoří tabulky, pokud ještě neexistují. Volat při startu aplikace.</summary>
    Task InitializeAsync();

    /// <summary>Vrátí všechny konverzace seřazené od nejnovější.</summary>
    Task<IReadOnlyList<ConversationRecord>> LoadAllConversationsAsync();

    /// <summary>Vrátí zprávy konverzace seřazené dle OrderIndex.</summary>
    Task<IReadOnlyList<MessageRecord>> LoadMessagesAsync(string conversationId);

    /// <summary>INSERT OR REPLACE — funguje jako upsert pro nové i upravené konverzace.</summary>
    Task SaveConversationAsync(ConversationRecord conversation);

    /// <summary>Přidá zprávu. Pokud Id existuje, přepíše obsah (pro update streaming výsledku).</summary>
    Task SaveMessageAsync(MessageRecord message);

    /// <summary>Smaže konverzaci i všechny její zprávy.</summary>
    Task DeleteConversationAsync(string conversationId);

    /// <summary>Smaže všechny zprávy konverzace s OrderIndex >= fromOrderIndex.</summary>
    Task DeleteMessagesFromIndexAsync(string conversationId, int fromOrderIndex);

    /// <summary>
    /// Vymaže VŠECHNY konverzace a zprávy z databáze. Destruktivní — volat
    /// jen po explicitním potvrzení v UI. Bezpečné volat za běhu (není to drop
    /// table, jen DELETE FROM, takže schema zůstává a appka funguje dál).
    /// </summary>
    Task ClearAllConversationsAsync();
}
