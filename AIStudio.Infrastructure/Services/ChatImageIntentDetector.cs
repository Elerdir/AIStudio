using System.Text.RegularExpressions;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Keyword-based klasifikátor — žádné LLM volání, žádná latence.
///
/// <para>Logika:</para>
/// <list type="number">
/// <item>Pokud poslední assistantská zpráva měla obrázek a aktuální zpráva
/// obsahuje edit-keywords ("změň", "uprav", "udělej to v noci", "change",
/// "make it", "now with") → <see cref="ChatImageIntent.EditPreviousImage"/>.</item>
/// <item>Pokud zpráva obsahuje "silné" obrazové sloveso ("nakresli", "namaluj",
/// "vyfotografuj", "draw", "paint") → <see cref="ChatImageIntent.GenerateImage"/>.
/// (Tato slovesa jsou už samy o sobě obrazové, nepotřebují substantivum.)</item>
/// <item>Pokud zpráva obsahuje generic sloveso ("vygeneruj", "vytvoř", "udělej",
/// "generate", "create", "make") + obrazové substantivum ("obrázek", "foto",
/// "scéna", "image", "picture") → <see cref="ChatImageIntent.GenerateImage"/>.
/// Generic slovesa samy bez substantiva by mohly znamenat cokoliv ("vygeneruj
/// kód", "vytvoř plán") — proto vyžadujeme i image noun.</item>
/// <item>Jinak → <see cref="ChatImageIntent.Chat"/>.</item>
/// </list>
///
/// <para>Pozor: false negatives (nezachytí "ukaž mi krásnou krajinu") jsou
/// preferované před false positives ("vygeneruj funkci v Pythonu" by se nemělo
/// klasifikovat jako image). UI má override toggle pro manuální korekci.</para>
/// </summary>
public sealed class ChatImageIntentDetector : IChatImageIntentDetector
{
    // "Silná" slovesa — sama o sobě implikují image, nepotřebují podstatné jméno.
    // Pokrýváme imperativ + infinitiv (uživatel může napsat "Můžeš mi nakreslit…?").
    // Generic slovesa záměrně nedáváme infinitiv — "vytvořit" / "udělat" /
    // "vygenerovat" by chytaly false positives jako "vytvořit funkci".
    private static readonly string[] StrongImageVerbs =
    {
        // CZ imperativ
        "nakresli", "nakresly", "namaluj", "vyfotografuj", "vyfoť",
        // CZ infinitiv
        "nakreslit", "namalovat", "vyfotografovat", "vyfotit",
        // EN (anglické imperativ = infinitiv)
        "draw", "paint", "sketch", "illustrate",
    };

    // Generická slovesa — vyžadují obrazové substantivum, aby se počítala jako image intent
    private static readonly string[] GenericVerbs =
    {
        "vygeneruj", "vytvoř", "udělej", "generuj", "ukaž",
        "generate", "create", "make", "show", "render",
    };

    // Obrazová substantiva (česky se musí pokrýt pády)
    private static readonly string[] ImageNouns =
    {
        "obrázek", "obrázku", "obrazek", "obrazku",
        "foto", "fotku", "fotografii", "fotografie",
        "scénu", "scenu", "scéna",
        "ilustraci", "ilustrace",
        "kresbu", "kresba",
        "image", "picture", "photo", "photograph", "scene", "illustration", "drawing", "sketch",
    };

    // Edit follow-up keywords — typicky používané pro úpravu předchozího obrázku
    private static readonly string[] EditKeywords =
    {
        "změň", "zmen", "uprav", "udělej to", "udelej to",
        "ale s", "ale bez", "místo", "misto", "místě", "miste",
        "ještě jednou", "jeste jednou", "znovu ale",
        "change", "edit", "modify", "make it", "but with", "but without",
        "instead", "now with", "again but",
    };

    public ChatImageIntent Detect(string userMessage, bool lastAssistantHadImage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return ChatImageIntent.Chat;

        var lower = userMessage.ToLowerInvariant();

        // 1) Edit má prioritu — ale jen pokud opravdu navazujeme na obrázek
        if (lastAssistantHadImage && ContainsAnyWord(lower, EditKeywords))
            return ChatImageIntent.EditPreviousImage;

        // 2) Silná slovesa stačí sama
        if (ContainsAnyWord(lower, StrongImageVerbs))
            return ChatImageIntent.GenerateImage;

        // 3) Generické sloveso + obrazové substantivum
        if (ContainsAnyWord(lower, GenericVerbs) && ContainsAnyWord(lower, ImageNouns))
            return ChatImageIntent.GenerateImage;

        return ChatImageIntent.Chat;
    }

    /// <summary>
    /// Hledá kterýkoliv z <paramref name="keywords"/> jako whole-word match
    /// ve <paramref name="haystack"/>. Slova s mezerami se hledají jako substring
    /// (regex \b funguje jen na alfanum hranicích, ne na "to "). Slova bez mezer
    /// se hledají s \b na obou stranách.
    /// </summary>
    private static bool ContainsAnyWord(string haystack, string[] keywords)
    {
        foreach (var k in keywords)
        {
            if (k.Contains(' '))
            {
                // Vícezslovné fráze — substring match s mezerami / interpunkcí kolem
                if (haystack.Contains(k))
                    return true;
            }
            else
            {
                // Whole-word match — \b nezná české znaky správně, ale ToLowerInvariant
                // + ruční zarážky kolem nealfanumeric znaků je rychlé a stačí na MVP.
                var pattern = $@"(^|[^\p{{L}}\p{{N}}]){Regex.Escape(k)}([^\p{{L}}\p{{N}}]|$)";
                if (Regex.IsMatch(haystack, pattern))
                    return true;
            }
        }
        return false;
    }
}
