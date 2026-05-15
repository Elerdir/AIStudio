namespace AIStudio.Core.Services;

/// <summary>
/// Hrubá odhad počtu tokenů pro českou + anglickou konverzaci bez načítání
/// skutečného tokenizéru. Slouží pro UI ukazatele („Použito ~3.2k/8k tokenů"),
/// NIKOLIV pro reálné context-limit rozhodnutí — k tomu by bylo potřeba volat
/// LlamaSharp tokenizér konkrétního modelu.
///
/// Algoritmus: 1 token ≈ 4 znaky textu. Pro anglické instrukce je to mírně
/// nadhodnoceno (~3.5 char/token), pro českou diakritikou ovládanou prózu
/// mírně podhodnoceno (~4.5 char/token). Pro UI varování (75 %/90 %) to
/// stačí — žádný tokenizer není zdarma a startup latence by byla horší
/// než pár procent přesnosti.
///
/// Pure static class — žádný stav, žádné side effecty, žádná Avalonia/UI
/// dependence. Bezpečné volat z kteréhokoliv threadu.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Konstanta — průměrný počet znaků na token.</summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// Odhadne počet tokenů jednoho textu. Pro prázdný/null vstup vrací 0.
    /// </summary>
    public static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // Round-down, aby krátké zprávy nedostaly nepoctivý overhead.
        // 4 znaky = 1 token, 7 znaků = 1 token, 8 znaků = 2 tokeny.
        return text.Length / CharsPerToken;
    }

    /// <summary>
    /// Odhadne součet tokenů přes všechny zprávy konverzace. Null vstup → 0.
    /// </summary>
    /// <remarks>
    /// Záměrně ignoruje role overhead (system/user/assistant header markery).
    /// V reálné chat template Llama 3.1 to vyjde na ~5–10 tokenů/zprávu navíc,
    /// ale to ve výpočtu zatím nemodelujeme — UI bar by jinak rostl skokem
    /// při krátké odpovědi a uživatel by se divil. Až budeme integrovat
    /// reálný tokenizér, tohle bude jeden z prvních zlepšováků.
    /// </remarks>
    public static int EstimateMessages(IEnumerable<string?>? messageContents)
    {
        if (messageContents is null) return 0;
        var total = 0;
        foreach (var c in messageContents)
            total += EstimateText(c);
        return total;
    }

    /// <summary>
    /// Procento využití context window — clampnuto na [0, 100].
    /// Při maxTokens=0 (uživatel ještě nezvolil model) vrací 0, aby UI bar nezhroutil.
    /// </summary>
    public static double UsagePercent(int estimatedTokens, int maxTokens)
    {
        if (maxTokens <= 0)        return 0;
        if (estimatedTokens <= 0)  return 0;
        var pct = estimatedTokens * 100.0 / maxTokens;
        return pct > 100 ? 100 : pct;
    }
}
