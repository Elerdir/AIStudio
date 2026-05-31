using System.Diagnostics;
using System.Text;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Konfigurace pro <see cref="ProcessRunner.RunAsync"/>.
/// </summary>
internal sealed class ProcessRunOptions
{
    public required string  FileName         { get; init; }
    public required string  Arguments        { get; init; }
    public string?          WorkingDirectory { get; init; }

    /// <summary>
    /// Když true (default), nastaví <c>PYTHONUNBUFFERED=1</c>, <c>PYTHONIOENCODING=utf-8</c>
    /// a <c>PYTHONUTF8=1</c> + čte stdout/stderr jako UTF-8. Nezbytné pro Python
    /// subprocess na Windows s ne-ASCII locale (cp1250) — jinak crash na CJK znacích.
    /// </summary>
    public bool             Utf8Python       { get; init; } = true;

    /// <summary>Kolik posledních řádků stdout/stderr držet v ring bufferu pro diagnostiku.</summary>
    public int              TailSize         { get; init; } = 50;

    /// <summary>Extra env proměnné (přepíšou existující). Null = žádné.</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnv { get; init; }
}

/// <summary>Výsledek <see cref="ProcessRunner.RunAsync"/>.</summary>
internal sealed record ProcessRunResult(int ExitCode, IReadOnlyList<string> TailLines)
{
    public bool   Success => ExitCode == 0;

    /// <summary>Posledních N řádků jako jeden blok — pro chybové hlášky a logy.</summary>
    public string TailText => TailLines.Count == 0
        ? string.Empty
        : string.Join('\n', TailLines);
}

/// <summary>
/// Jednotné, řízené spouštění externích procesů (Python skripty, git, native CLI).
///
/// <para>Konsoliduje boilerplate, který se dříve opakoval v <see cref="SdScriptsLoraTrainer"/>,
/// <see cref="BlipCaptionService"/>, <see cref="LoraTrainerDependencyService"/> i jinde:
/// UTF-8 encoding setup, async čtení stdout+stderr, ring buffer posledních řádků pro
/// diagnostiku, cancellation s kill celého process tree.</para>
///
/// <para>Per-konzument logika (parsování progressu, eskalace log levelu) zůstává
/// v <paramref name="onLine"/> callbacku — ProcessRunner ho volá pro každý řádek
/// a sám se stará jen o infrastrukturu.</para>
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Spustí proces, streamuje stdout/stderr přes <paramref name="onLine"/>, čeká na konec.
    /// Při zrušení přes <paramref name="ct"/> zabije celý process tree.
    /// </summary>
    /// <param name="onLine">
    /// Volán pro každý řádek stdout i stderr (na thread-poolu, ne UI). Caller si v něm
    /// řeší logování, progress parsing apod. Ring buffer tail se plní automaticky bez
    /// ohledu na callback.
    /// </param>
    public static async Task<ProcessRunResult> RunAsync(
        ProcessRunOptions options,
        Action<string>?   onLine = null,
        CancellationToken ct     = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = options.FileName,
            Arguments              = options.Arguments,
            WorkingDirectory       = options.WorkingDirectory ?? string.Empty,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        if (options.Utf8Python)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding  = Encoding.UTF8;
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"]       = "1";
        }

        if (options.ExtraEnv is not null)
            foreach (var (k, v) in options.ExtraEnv)
                psi.EnvironmentVariables[k] = v;

        using var p = new Process { StartInfo = psi };

        var tail     = new Queue<string>(options.TailSize);
        var tailLock = new object();

        void Handle(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (tailLock)
            {
                if (tail.Count >= options.TailSize) tail.Dequeue();
                tail.Enqueue(line);
            }
            try { onLine?.Invoke(line); }
            catch (Exception ex) { Log.Warning(ex, "ProcessRunner: onLine callback vyhodil výjimku"); }
        }

        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived  += (_, e) => Handle(e.Data);

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception ex) { Log.Warning(ex, "ProcessRunner: kill po cancelu selhal"); }
            throw;
        }

        List<string> tailSnapshot;
        lock (tailLock) tailSnapshot = tail.ToList();
        return new ProcessRunResult(p.ExitCode, tailSnapshot);
    }
}
