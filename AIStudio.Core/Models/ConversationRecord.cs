namespace AIStudio.Core.Models;

/// <summary>
/// Čistý POCO record pro uložení/načtení konverzace z databáze.
/// Neobsahuje žádné závislosti na UI frameworku.
/// </summary>
public record ConversationRecord(
    string   Id,
    string   Title,
    string   ModelName,
    int      MaxTokens,
    string   SystemPrompt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool     IsPinned          = false,
    float    Temperature       = 0.7f,
    bool     IsThinkingEnabled = true,
    string   Draft             = "");
