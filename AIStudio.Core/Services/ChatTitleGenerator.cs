using System.Text.RegularExpressions;

namespace AIStudio.Core.Services;

/// <summary>
/// Pure logika auto-pojmenování konverzace LLM-em. Dříve zapečená v
/// <c>ChatPageViewModel.MaybeAutoRenameAsync</c> — detekce default titulu,
/// sestavení promptu a hlavně čištění LLM výstupu (uvozovky, prefixy, délka)
/// se nedalo testovat bez UI + LLM. Tady je čistá, deterministická.
/// </summary>
public static class ChatTitleGenerator
{
    /// <summary>Default title pattern „Chat N" — takové se smí auto-přejmenovat.</summary>
    private static readonly Regex DefaultTitleRx = new(@"^Chat \d+$", RegexOptions.Compiled);

    /// <summary>Prefixy, které malé LLM občas přidá před samotný název.</summary>
    private static readonly string[] StripPrefixes = { "Název:", "Title:", "Pojmenování:" };

    private static readonly char[] TrimChars =
        { '"', '\'', '«', '»', '„', '"', ' ', '\n', '\r', '\t', ':', '.', '-' };

    private const int MaxTitleLength = 60;
    private const int MinTitleLength = 2;

    /// <summary>
    /// True pokud title vypadá jako auto-generovaný default („Chat 1", „Chat 42").
    /// Ručně přejmenované konverzace vrátí false — ty se nesmí přepsat.
    /// </summary>
    public static bool IsDefaultTitle(string? title) =>
        !string.IsNullOrEmpty(title) && DefaultTitleRx.IsMatch(title);

    /// <summary>
    /// Sestaví (system, user) prompt pro pojmenování — model dostane první výměnu
    /// a má vrátit 2-4 slovní český název. Vstupy se ořezávají na 800 znaků, aby
    /// se malým modelům nezahltil kontext.
    /// </summary>
    public static IReadOnlyList<(string Role, string Content)> BuildPrompt(
        string firstUserMessage, string firstAssistantMessage)
    {
        const string system =
            "Jsi nástroj pro pojmenování konverzace. Vrátíš JEN 2-4 slovní český " +
            "název odpovídající tématu. Žádné uvozovky, žádné vysvětlení, žádný " +
            "markdown, žádné slovo \"chat\" nebo \"konverzace\". Ideálně " +
            "podstatná jména. Pouze samotný název.";

        var user =
            "Konverzace:\n\n" +
            $"Uživatel: {Truncate(firstUserMessage, 800)}\n\n" +
            $"Asistent: {Truncate(firstAssistantMessage, 800)}\n\n" +
            "Krátký název:";

        return new[] { ("system", system), ("user", user) };
    }

    /// <summary>
    /// Vyčistí surový LLM výstup na použitelný title: ořízne uvozovky/interpunkci,
    /// vezme jen první řádek, odstraní „Název:"/„Title:" prefix, ořeže na 60 znaků.
    /// Vrátí <c>null</c> pokud výsledek není použitelný (prázdný / kratší než 2 znaky).
    /// </summary>
    public static string? CleanResponse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var title = raw.Trim(TrimChars)
                       .Split('\n', '\r')[0]   // jen první řádek
                       .Trim();

        foreach (var prefix in StripPrefixes)
        {
            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                title = title[prefix.Length..].Trim();
        }

        // Druhý trim — po odstranění prefixu mohou zůstat uvozovky/mezery
        title = title.Trim(TrimChars).Trim();

        if (string.IsNullOrWhiteSpace(title)) return null;
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength].Trim();
        if (title.Length < MinTitleLength) return null;

        return title;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= max ? s : s[..max] + "…";
}
