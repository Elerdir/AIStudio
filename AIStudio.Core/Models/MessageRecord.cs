namespace AIStudio.Core.Models;

/// <summary>
/// Čistý POCO record pro uložení/načtení zprávy z databáze.
/// </summary>
public record MessageRecord(
    string   Id,
    string   ConversationId,
    string   Role,         // "user" | "assistant" | "system"
    string   Content,
    DateTime Timestamp,
    int      OrderIndex);
