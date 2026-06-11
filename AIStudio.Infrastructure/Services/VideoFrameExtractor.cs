using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Vytahuje jednotlivé snímky z videa přes ffmpeg (binárka se resolvne přes
/// <see cref="FfmpegVideoJoiner.ResolveFfmpeg"/> — imageio-ffmpeg z ComfyUI, fallback PATH).
/// Slouží pipeline „video → LoRA" (extrakce kandidátů podle
/// <see cref="AIStudio.Core.Services.VideoFrameSamplingPlanner"/>).
///
/// <para>Čistá logika (parsování délky, stavba argumentů) je vytažená do statických metod,
/// aby šla unit-testovat bez spuštění ffmpegu.</para>
/// </summary>
public static class VideoFrameExtractor
{
    private static readonly Regex DurationRegex =
        new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>
    /// Vyparsuje délku videa z ffmpeg stderr (řádek <c>Duration: HH:MM:SS.ss</c>).
    /// Null když řádek chybí / nejde naparsovat.
    /// </summary>
    public static TimeSpan? ParseDuration(string? ffmpegOutput)
    {
        if (string.IsNullOrEmpty(ffmpegOutput)) return null;
        var m = DurationRegex.Match(ffmpegOutput);
        if (!m.Success) return null;

        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var s = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return TimeSpan.FromSeconds(h * 3600 + min * 60 + s);
    }

    /// <summary>
    /// Argumenty ffmpegu pro vytažení jednoho snímku v čase <paramref name="ts"/>.
    /// <c>-ss</c> je <b>před</b> <c>-i</c> (rychlé keyframe seekování), <c>-frames:v 1</c>
    /// uloží jeden snímek, <c>-q:v 2</c> = vysoká kvalita JPG.
    /// </summary>
    public static IReadOnlyList<string> BuildExtractArgs(string videoPath, TimeSpan ts, string outPath) => new[]
    {
        "-y", "-hide_banner", "-loglevel", "error",
        "-ss", ts.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-i", videoPath,
        "-frames:v", "1",
        "-q:v", "2",
        outPath,
    };

    /// <summary>Zjistí délku videa (spustí <c>ffmpeg -i</c> a vyparsuje stderr). Null při chybě.</summary>
    public static async Task<TimeSpan?> ProbeDurationAsync(string ffmpeg, string videoPath, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ffmpeg,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(videoPath);
            // Žádný výstupní soubor → ffmpeg skončí s kódem 1, ale Duration vytiskne do stderr.

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return ParseDuration(stderr);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoFrameExtractor: probe délky videa selhal ({Video})", videoPath);
            return null;
        }
    }

    /// <summary>Vytáhne jeden snímek do <paramref name="outPath"/>. True při úspěchu.</summary>
    public static async Task<bool> ExtractFrameAsync(
        string ffmpeg, string videoPath, TimeSpan ts, string outPath, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ffmpeg,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in BuildExtractArgs(videoPath, ts, outPath)) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return false;
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || !File.Exists(outPath))
            {
                Log.Warning("VideoFrameExtractor: extrakce snímku @ {Ts}s selhala (kód {Code}): {Err}",
                    ts.TotalSeconds, proc.ExitCode, Truncate(stderr, 300));
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "VideoFrameExtractor: extrakce snímku selhala ({Video} @ {Ts}s)", videoPath, ts.TotalSeconds);
            return false;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
