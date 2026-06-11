namespace AIStudio.Core.Services;

/// <summary>
/// Spočítá, ze kterých časových pozic videa vytáhnout kandidátní snímky pro LoRA dataset.
/// Vzorkuje <b>rovnoměrně</b> přes „užitečnou“ část videa (vynechá úvod/závěr dle voleb)
/// a používá <b>midpoint vzorkování</b> — značky leží uprostřed N stejných dílů, takže nikdy
/// netrefí přesně první/poslední snímek (intro/outro/černá) a sousední snímky jsou od sebe
/// co nejdál (menší vizuální duplicita).
///
/// <para>Čistá logika bez I/O → plně testovatelné. Extrakci samotnou (ffmpeg) dělá
/// Infrastructure podle těchto značek.</para>
/// </summary>
public static class VideoFrameSamplingPlanner
{
    /// <summary>Strop kandidátů na jedno video (ochrana proti zahlcení UI a disku).</summary>
    public const int MaxFramesPerVideo = 200;

    /// <summary>
    /// Vrátí časové značky snímků k extrakci z videa délky <paramref name="durationSeconds"/>.
    /// Prázdný seznam pro nulovou/zápornou délku.
    /// </summary>
    public static IReadOnlyList<TimeSpan> Plan(double durationSeconds, Models.FrameSamplingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (durationSeconds <= 0) return Array.Empty<TimeSpan>();

        var count = Math.Clamp(options.FramesPerVideo, 1, MaxFramesPerVideo);

        // Skip frakce do rozumných mezí; když by okno vyšlo neplatné (součet ≥ 1),
        // spadneme na celé video.
        var skipStart = Math.Clamp(options.SkipStartFraction, 0.0, 0.49);
        var skipEnd   = Math.Clamp(options.SkipEndFraction,   0.0, 0.49);

        var windowStart = durationSeconds * skipStart;
        var windowEnd   = durationSeconds * (1.0 - skipEnd);
        var windowLen   = windowEnd - windowStart;
        if (windowLen <= 0)
        {
            windowStart = 0;
            windowLen   = durationSeconds;
        }

        var stamps = new List<TimeSpan>(count);
        for (var i = 0; i < count; i++)
        {
            var frac = (i + 0.5) / count;                 // midpoint i-tého dílu
            var t    = windowStart + frac * windowLen;
            stamps.Add(TimeSpan.FromSeconds(t));
        }
        return stamps;
    }
}
