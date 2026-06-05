using System.Diagnostics;
using System.Text;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Spojí více MP4 segmentů do jednoho výsledného videa přes ffmpeg <c>concat</c> demuxer
/// (stream copy, <c>-c copy</c> — bez re-enkódování, takže rychle a bez ztráty kvality;
/// funguje protože všechny segmenty mají shodný kodek/rozlišení/FPS z téhož VHS nastavení).
///
/// <para>ffmpeg binárku <b>nestahuje sám</b> — používá tu, kterou už přináší ComfyUI přes
/// <c>imageio-ffmpeg</c> (instaluje VideoHelperSuite), s fallbackem na ffmpeg v PATH.
/// Když ffmpeg nenajde, vrátí false a volající ponechá samostatné segmenty.</para>
/// </summary>
public static class FfmpegVideoJoiner
{
    /// <summary>
    /// Najde použitelný ffmpeg: nejdřív <c>imageio-ffmpeg</c> binárku vedle embedded Pythonu
    /// ComfyUI (<paramref name="pythonExePath"/>), pak ffmpeg v PATH. Vrací cestu nebo null.
    /// </summary>
    public static string? ResolveFfmpeg(string? pythonExePath)
    {
        // 1) imageio-ffmpeg binárka v site-packages embedded Pythonu.
        try
        {
            var pyDir = string.IsNullOrWhiteSpace(pythonExePath) ? null : Path.GetDirectoryName(pythonExePath);
            if (!string.IsNullOrEmpty(pyDir))
            {
                var binDir = Path.Combine(pyDir, "Lib", "site-packages", "imageio_ffmpeg", "binaries");
                if (Directory.Exists(binDir))
                {
                    var exe = Directory.EnumerateFiles(binDir, "ffmpeg*")
                                       .FirstOrDefault(f =>
                                       {
                                           var ext = Path.GetExtension(f);
                                           return ext is "" or ".exe";
                                       });
                    if (exe is not null) return exe;
                }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "FfmpegVideoJoiner: hledání imageio-ffmpeg selhalo"); }

        // 2) ffmpeg v PATH (Windows .exe i unixová varianta).
        var candidate = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), candidate);
                if (File.Exists(full)) return full;
            }
            catch { /* neplatný PATH segment */ }
        }
        return null;
    }

    /// <summary>
    /// Spojí <paramref name="segmentPaths"/> (v pořadí) do <paramref name="outputPath"/>.
    /// Vrací true při úspěchu. Best-effort — chyba se zaloguje a vrátí false.
    /// </summary>
    public static async Task<bool> JoinAsync(
        IReadOnlyList<string> segmentPaths,
        string                outputPath,
        string?               pythonExePath,
        CancellationToken     ct = default)
    {
        if (segmentPaths is null || segmentPaths.Count == 0) return false;
        if (segmentPaths.Count == 1)
        {
            try { File.Copy(segmentPaths[0], outputPath, overwrite: true); return true; }
            catch (Exception ex) { Log.Warning(ex, "FfmpegVideoJoiner: kopie jediného segmentu selhala"); return false; }
        }

        var ffmpeg = ResolveFfmpeg(pythonExePath);
        if (ffmpeg is null)
        {
            Log.Warning("FfmpegVideoJoiner: ffmpeg nenalezen — segmenty zůstanou samostatné");
            return false;
        }

        // concat demuxer potřebuje textový seznam souborů: file '<absolutní cesta>'
        var listFile = Path.Combine(Path.GetTempPath(), $"aistudio_concat_{Guid.NewGuid():N}.txt");
        try
        {
            var sb = new StringBuilder();
            foreach (var seg in segmentPaths)
            {
                // apostrofy v cestě se v concat seznamu escapují jako '\''
                var escaped = seg.Replace("'", "'\\''");
                sb.Append("file '").Append(escaped).Append("'\n");
            }
            await File.WriteAllTextAsync(listFile, sb.ToString(), ct);

            var psi = new ProcessStartInfo
            {
                FileName               = ffmpeg,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("concat");
            psi.ArgumentList.Add("-safe");     psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(listFile);
            psi.ArgumentList.Add("-c");        psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add(outputPath);

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                Log.Warning("FfmpegVideoJoiner: ffmpeg se nepodařilo spustit");
                return false;
            }

            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                Log.Warning("FfmpegVideoJoiner: ffmpeg skončil s kódem {Code}: {Err}",
                    proc.ExitCode, Truncate(stderr, 600));
                return false;
            }

            return File.Exists(outputPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "FfmpegVideoJoiner: spojení segmentů selhalo");
            return false;
        }
        finally
        {
            try { if (File.Exists(listFile)) File.Delete(listFile); } catch { /* ignore */ }
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
