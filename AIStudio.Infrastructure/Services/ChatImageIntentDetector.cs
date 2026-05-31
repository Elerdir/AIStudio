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

    // Generická slovesa — vyžadují obrazové substantivum, aby se počítala jako image intent.
    // Pokrýváme imperativ + běžné konverzační obraty ("chci", "potřebuju", "dej mi"…),
    // protože "chci obrázek psa" je v chatu úplně přirozený obrat, jen není imperativ.
    // Generic slovesa záměrně NEdáváme infinitiv ("vytvořit", "udělat") — chytaly by
    // false positives jako "musíš vytvořit funkci".
    private static readonly string[] GenericVerbs =
    {
        // CZ — imperativ generování
        "vygeneruj", "vytvoř", "udělej", "generuj", "ukaž",
        // CZ — chtění / potřeba / žádost (s image noun = chce obrázek)
        "chci", "chtěl", "chtela", "chtěla", "potřebuju", "potrebuju", "potřebuji", "potrebuji",
        "dej", "pošli", "posli", "vrať", "vrat",
        // EN — generování
        "generate", "create", "make", "show", "render",
        // EN — chtění / žádost
        "want", "need", "give", "send",
    };

    // Obrazová substantiva — česky musíme pokrýt pády (akuzativ/genitiv hlavně).
    // Pokud uživatel napíše "vygeneruj obrázek" / "obrázku" / "obrázky" — všechno
    // znamená obrazový intent. Bez plurálu by věty typu "dej mi nějaké obrázky"
    // propadly do chatu.
    private static readonly string[] ImageNouns =
    {
        // CZ — obrázek (všechny pády + bez háčku pro toleranci klávesnice)
        "obrázek", "obrázku", "obrázky", "obrázkem", "obrázcích",
        "obrazek", "obrazku", "obrazky", "obrazkem",
        // CZ — foto / fotka / fotografie
        "foto", "fotka", "fotku", "fotky", "fotkou",
        "fotografii", "fotografie", "fotografií", "fotografiemi",
        // CZ — scéna
        "scéna", "scénu", "scény", "scéně", "scénou", "scenu", "scena",
        // CZ — ilustrace / kresba / malba
        "ilustraci", "ilustrace", "ilustrací",
        "kresbu", "kresba", "kresby",
        "malbu", "malba",
        "vizuál", "vizualizaci", "vizualizace",
        // EN
        "image", "images", "picture", "pictures", "pic", "pics",
        "photo", "photos", "photograph", "photographs",
        "scene", "scenes",
        "illustration", "illustrations", "drawing", "drawings", "sketch", "sketches",
        "artwork", "render", "renders", "visual", "visuals",
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

    // Tázací slova — pokud jimi zpráva ZAČÍNÁ, jde skoro jistě o otázku.
    // (Uprostřed věty je nehodnotíme, ať „udělej z toho co nejlepší kvalitu"
    // nespadne falešně do otázky kvůli slovu „co".)
    private static readonly string[] QuestionStartWords =
    {
        // CZ
        "co", "kdo", "kde", "kdy", "kolik", "proč", "proc", "jak", "jaký", "jaka", "jaká",
        "jaké", "jake", "jakou", "jakého", "jakeho", "čí", "ci", "který", "ktery", "která",
        "ktera", "které", "ktere", "kterou", "umíš", "umis", "můžeš", "muzes", "dokážeš", "dokazes",
        // EN
        "what", "whats", "who", "where", "when", "why", "how", "which", "whose",
        "is", "are", "can", "could", "does", "do", "describe", "tell",
    };

    // Popisné fráze/slovesa kdekoliv ve zprávě — jasná žádost o porozumění obrázku.
    private static readonly string[] QuestionPhrases =
    {
        // CZ
        "popiš", "popis", "popsat", "přečti", "precti", "přečíst", "precist", "co je na",
        "co je to", "co to je", "co vidíš", "co vidis", "co je v", "identifikuj", "rozpoznej",
        "vysvětli", "vysvetli", "analyzuj", "co znamená", "co znamena", "kolik je", "co obsahuje",
        // EN
        "describe", "what is", "what's", "whats in", "how many", "read this", "read the",
        "identify", "explain", "tell me", "what do you see", "can you see",
    };

    public bool IsImageQuestion(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return false;

        var lower = userMessage.ToLowerInvariant().TrimStart();

        // Otazník = jednoznačná otázka
        if (lower.Contains('?')) return true;

        // Popisné fráze kdekoliv
        if (ContainsAnyWord(lower, QuestionPhrases)) return true;

        // Tázací slovo na ZAČÁTKU zprávy
        var firstWord = new string(lower.TakeWhile(char.IsLetter).ToArray());
        return !string.IsNullOrEmpty(firstWord) && Array.IndexOf(QuestionStartWords, firstWord) >= 0;
    }

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
