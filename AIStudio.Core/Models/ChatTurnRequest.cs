namespace AIStudio.Core.Models;

/// <summary>
/// Zadání jednoho LLM „tahu" v chatu — vstup pro <see cref="Interfaces.IChatTurnService"/>.
/// Nese vše potřebné k sestavení historie a streamování odpovědi, bez vazby na UI typy
/// (<c>ChatMessage</c>). <see cref="PriorMessages"/> jsou předchozí zprávy konverzace
/// (bez aktuálního prázdného asistentského placeholderu) jako primitivy role/obsah.
/// </summary>
public sealed record ChatTurnRequest(
    string?                                       SystemPrompt,
    string                                        ModelName,
    bool                                          ThinkingEnabled,
    IReadOnlyList<(string Role, string Content)>  PriorMessages,
    int                                           MaxTokens,
    double                                        Temperature);
