namespace AIStudio.Core.Services;

/// <summary>
/// „Smart" rozplánování dlouhého videa na krátké Wan 2.1 segmenty. Wan 2.1 zvládne
/// nativně jen ~5 s (max 81 snímků) na jeden běh, takže delší video se skládá z více
/// segmentů řetězených přes <b>image→video z posledního snímku</b> předchozího segmentu.
///
/// <para>Kvalita: každé předání (handoff) mezi segmenty mírně driftuje (barvy/detail),
/// proto plánovač <b>minimalizuje počet segmentů</b> — dělá je co nejdelší (blízko stropu
/// 81 snímků) a rovnoměrné. Délka každého segmentu je vždy <c>4n+1</c> (Wan požadavek na
/// délku latentu).</para>
///
/// <para>Čistá logika bez závislostí → plně testovatelné.</para>
/// </summary>
public static class VideoSegmentPlanner
{
    /// <summary>Strop Wan 2.1 na jeden běh (~5 s při 16 FPS). 81 = 4·20 + 1.</summary>
    public const int MaxFramesPerSegment = 81;

    /// <summary>Minimální smysluplná délka segmentu (4·1 + 1).</summary>
    public const int MinFramesPerSegment = 5;

    /// <summary>
    /// Zaokrouhlí počet snímků na nejbližší platnou Wan délku (<c>4n+1</c>), nejméně
    /// <see cref="MinFramesPerSegment"/>.
    /// </summary>
    public static int RoundToWanLength(int frames)
    {
        if (frames <= MinFramesPerSegment) return MinFramesPerSegment;
        var n = (int)Math.Round((frames - 1) / 4.0, MidpointRounding.AwayFromZero);
        var rounded = n * 4 + 1;
        return rounded < MinFramesPerSegment ? MinFramesPerSegment : rounded;
    }

    /// <summary>
    /// Rozplánuje cílovou délku videa (sekundy × FPS) na segmenty. Vrací počty snímků
    /// jednotlivých segmentů (každý <c>4n+1</c>, ≤ <paramref name="maxFrames"/>).
    ///
    /// <para>Zohledňuje <b>překryv při řetězení</b>: druhý a další segment začínají
    /// posledním snímkem předchozího (ten se nepočítá jako nový), takže efektivní délka
    /// celku ≈ <c>len₀ + Σ(lenₖ − 1)</c>.</para>
    /// </summary>
    /// <param name="targetSeconds">Cílová délka výsledného videa v sekundách.</param>
    /// <param name="fps">Snímky za sekundu (Wan generuje nativně ~16).</param>
    /// <param name="maxFrames">Strop snímků na segment (default 81 = Wan limit).</param>
    public static IReadOnlyList<int> Plan(int targetSeconds, int fps, int maxFrames = MaxFramesPerSegment)
    {
        if (fps <= 0) fps = 16;
        if (targetSeconds <= 0) targetSeconds = 1;

        maxFrames = RoundToWanLength(Math.Clamp(maxFrames, MinFramesPerSegment, MaxFramesPerSegment));

        var totalFrames = Math.Max(MinFramesPerSegment, (int)Math.Round(targetSeconds * (double)fps));

        // Kapacita N segmentů s překryvem: N·maxFrames − (N−1) snímků.
        // Najdi nejmenší N, které pokryje totalFrames → nejméně segmentů → nejméně driftu.
        var n = 1;
        while ((long)n * maxFrames - (n - 1) < totalFrames) n++;

        // Rovnoměrná délka segmentu: potřebujeme (totalFrames + (N−1)) raw snímků rozdělit na N.
        var rawLen = (int)Math.Ceiling((totalFrames + (n - 1)) / (double)n);
        var len    = Math.Clamp(RoundToWanLength(rawLen), MinFramesPerSegment, maxFrames);

        var segments = new List<int>(n);
        for (var i = 0; i < n; i++) segments.Add(len);
        return segments;
    }

    /// <summary>
    /// Odhad efektivní délky výsledku (sekundy) pro daný plán — kvůli překryvu je o málo
    /// kratší než prostý součet segmentů.
    /// </summary>
    public static double EffectiveSeconds(IReadOnlyList<int> segments, int fps)
    {
        if (segments is null || segments.Count == 0 || fps <= 0) return 0;
        var frames = segments[0];
        for (var i = 1; i < segments.Count; i++) frames += segments[i] - 1;
        return frames / (double)fps;
    }
}
