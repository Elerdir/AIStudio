using System.Globalization;

namespace AIStudio.Core.Services;

/// <summary>
/// Lidský formát pro byte counts ("1.5 GB", "234 MB", "12 KB"). Sdílený
/// helper, který předtím existoval duplicitně v ModelItemViewModel a
/// ChatMessage. Pravidla jsou stejné jako u Windows Explorer / `du -h`:
/// největší jednotka, která má hodnotu &gt; 0 v celé části (1023 KB by
/// se nezobrazovalo jako "1 MB" ale jako "1023 KB"), KB s F0 (žádné
/// desetiny), MB s F1, GB s F2.
///
/// <para><strong>Culture:</strong> používá <see cref="CultureInfo.InvariantCulture"/>
/// (tečka jako desetinný oddělovač). Pro file sizes je tečka konvence napříč
/// nástroji (Explorer, du, ls -lh, …). Tím je výstup deterministický pro
/// unit testy + konzistentní bez ohledu na OS locale.</para>
/// </summary>
public static class ByteFormatter
{
    private const long Kb = 1_024L;
    private const long Mb = 1_048_576L;
    private const long Gb = 1_073_741_824L;

    /// <summary>Formátuje na "B", "KB", "MB" nebo "GB" podle velikosti.</summary>
    public static string Format(long bytes)
    {
        var c = CultureInfo.InvariantCulture;
        return bytes switch
        {
            < Kb => string.Create(c, $"{bytes} B"),
            < Mb => string.Create(c, $"{bytes / (double)Kb:F0} KB"),
            < Gb => string.Create(c, $"{bytes / (double)Mb:F1} MB"),
            _    => string.Create(c, $"{bytes / (double)Gb:F2} GB"),
        };
    }
}
