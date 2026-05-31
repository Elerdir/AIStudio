namespace AIStudio.Core.Services;

/// <summary>
/// Pure logika „compactu" konverzace — obdoba <c>/compact</c> v Claude Code.
/// Když konverzace naroste, nejstarší zprávy se LLM-em shrnou do jednoho
/// kompaktního summary, čímž drasticky klesne počet tokenů posílaných do
/// kontextu, ale model si „pamatuje" o čem byla řeč. Posledních pár zpráv
/// zůstává doslovně, aby navázání bylo přirozené.
///
/// <para>Tady je jen čistá logika (rozdělení zpráv, sestavení promptu,
/// formátování výstupu) — orchestraci (LLM stream, DB přepis) řeší
/// <c>ChatPageViewModel.CompactConversationAsync</c>.</para>
/// </summary>
public static class ConversationCompactor
{
    /// <summary>Jedna zpráva konverzace — primitiv bez závislosti na UI typech.</summary>
    public sealed record Message(string Role, string Content);

    /// <summary>Kolik posledních zpráv zůstane doslovně (zbytek se shrne).</summary>
    public const int DefaultKeepRecent = 4;

    /// <summary>Pod tímto počtem zpráv compact nemá smysl (málo co shrnovat).</summary>
    public const int MinMessagesToCompact = 6;

    /// <summary>Hlavička, podle které UI i caller poznají compact summary zprávu.</summary>
    public const string SummaryHeader = "📝 Shrnutí předchozí konverzace";

    /// <summary>
    /// True pokud má smysl konverzaci kompaktovat: musí mít aspoň
    /// <see cref="MinMessagesToCompact"/> zpráv a víc, než kolik se ponechá +1
    /// (jinak by se shrnovala jediná zpráva nebo nic).
    /// </summary>
    public static bool CanCompact(int messageCount, int keepRecent = DefaultKeepRecent)
        => messageCount >= MinMessagesToCompact && messageCount > Math.Max(0, keepRecent) + 1;

    /// <summary>
    /// Rozdělí zprávy na ty, které se mají shrnout (starší), a ty, které
    /// zůstanou doslovně (posledních <paramref name="keepRecent"/>).
    /// </summary>
    public static (IReadOnlyList<Message> ToSummarize, IReadOnlyList<Message> ToKeep) Split(
        IReadOnlyList<Message> messages, int keepRecent = DefaultKeepRecent)
    {
        if (messages is null || messages.Count == 0)
            return (Array.Empty<Message>(), Array.Empty<Message>());

        keepRecent  = Math.Max(0, keepRecent);
        var keep    = Math.Min(keepRecent, messages.Count);
        var splitAt = messages.Count - keep;

        var toSummarize = messages.Take(splitAt).ToList();
        var toKeep      = messages.Skip(splitAt).ToList();
        return (toSummarize, toKeep);
    }

    /// <summary>
    /// Sestaví (system, user) prompt pro LLM: dostane přepis starších zpráv
    /// a má vrátit věcné shrnutí, které zachová fakta, rozhodnutí a kontext
    /// potřebný pro pokračování.
    /// </summary>
    public static IReadOnlyList<(string Role, string Content)> BuildSummaryPrompt(
        IEnumerable<Message> toSummarize)
    {
        const string system =
            "Jsi nástroj pro shrnutí konverzace. Vytvoříš stručné, věcné shrnutí " +
            "v češtině, které zachová VŠECHNA důležitá fakta, jména, čísla, " +
            "rozhodnutí, kód a kontext potřebný pro pokračování. Piš v odrážkách. " +
            "Žádný úvod ani závěr, jen samotné shrnutí. Nevynechávej technické detaily.";

        var transcript = string.Join("\n\n", toSummarize.Select(FormatLine));

        var user =
            "Shrň následující část konverzace tak, aby si asistent mohl pamatovat " +
            "kontext, i když původní zprávy zmizí:\n\n" +
            transcript +
            "\n\nShrnutí:";

        return new[] { ("system", system), ("user", user) };
    }

    /// <summary>
    /// Zabalí surové LLM shrnutí do summary zprávy s hlavičkou. Při prázdném
    /// výstupu vrátí best-effort fallback, ať compact nikdy nevytvoří prázdnou
    /// zprávu (to by ztratilo kontext).
    /// </summary>
    public static string FormatSummary(string? rawSummary)
    {
        var body = (rawSummary ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(body))
            body = "*(shrnutí se nepodařilo vygenerovat — starší zprávy byly zkráceny)*";

        return $"{SummaryHeader}\n\n{body}";
    }

    /// <summary>True pokud obsah zprávy vypadá jako už vygenerované compact summary.</summary>
    public static bool IsSummary(string? content)
        => !string.IsNullOrEmpty(content) && content.StartsWith(SummaryHeader, StringComparison.Ordinal);

    private static string FormatLine(Message m)
    {
        var label = m.Role?.ToLowerInvariant() switch
        {
            "assistant" => "Asistent",
            "system"    => "Kontext",
            _           => "Uživatel",
        };
        return $"{label}: {m.Content}";
    }
}
