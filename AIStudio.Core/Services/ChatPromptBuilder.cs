namespace AIStudio.Core.Services;

/// <summary>
/// Pure logika sestavení chat historie pro LLM inference. Dříve byla zapečená
/// v <c>ChatPageViewModel.BuildHistory</c> (1697-řádkový God-object), kde nešla
/// unit-testovat bez UI. Tady je čistá funkce nad primitivy — žádná závislost
/// na Avalonia / ViewModel typech.
///
/// <para>Řeší: výchozí system prompt, per-konverzaci override, Qwen3 thinking
/// mode (<c>/no_think</c> prefix), a pořadí zpráv.</para>
/// </summary>
public static class ChatPromptBuilder
{
    /// <summary>Výchozí system prompt, když konverzace nemá vlastní.</summary>
    public const string DefaultSystemPrompt =
        "Jsi AI asistent. Odpovídáš přesně, stručně a v jazyce uživatele.";

    /// <summary>True pokud je to model ze série Qwen3 (má thinking mode).</summary>
    public static bool IsQwen3Model(string? modelName) =>
        !string.IsNullOrEmpty(modelName) &&
        modelName.Contains("qwen3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sestaví historii pro <c>ILlamaService.ChatAsync</c>: system prompt (s
    /// případným <c>/no_think</c> prefixem pro Qwen3) následovaný předchozími
    /// zprávami v jejich pořadí.
    /// </summary>
    /// <param name="systemPromptOverride">
    /// Per-konverzaci system prompt. Prázdný/null → použije se <see cref="DefaultSystemPrompt"/>.
    /// </param>
    /// <param name="modelName">Pro detekci Qwen3 thinking mode.</param>
    /// <param name="thinkingEnabled">
    /// Když false A model je Qwen3, přidá se <c>/no_think</c> prefix (rychlejší,
    /// kratší odpovědi bez interního přemýšlení).
    /// </param>
    /// <param name="priorMessages">
    /// Předchozí zprávy konverzace jako (role, obsah). Role „user"/„assistant"/
    /// „system" se zachovají, neznámé spadnou na „user". Caller typicky předává
    /// všechny zprávy KROMĚ posledního prázdného assistant placeholderu.
    /// </param>
    public static List<(string Role, string Content)> BuildHistory(
        string?                                     systemPromptOverride,
        string?                                     modelName,
        bool                                        thinkingEnabled,
        IEnumerable<(string Role, string Content)>  priorMessages)
    {
        var history = new List<(string Role, string Content)>();

        var sysPrompt = string.IsNullOrWhiteSpace(systemPromptOverride)
            ? DefaultSystemPrompt
            : systemPromptOverride;

        if (IsQwen3Model(modelName) && !thinkingEnabled)
            sysPrompt = "/no_think\n" + sysPrompt;

        if (!string.IsNullOrEmpty(sysPrompt))
            history.Add(("system", sysPrompt));

        foreach (var (role, content) in priorMessages)
            history.Add((NormalizeRole(role), content));

        return history;
    }

    /// <summary>Sjednotí roli na „user"/„assistant"/„system", neznámé → „user".</summary>
    public static string NormalizeRole(string? role) => role?.ToLowerInvariant() switch
    {
        "assistant" => "assistant",
        "system"    => "system",
        "user"      => "user",
        _           => "user",
    };
}
