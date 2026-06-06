using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Vestavěný generátor obrázků přes <c>sd-cli.exe</c> (stable-diffusion.cpp CLI) — nezávislost
/// na ComfyUI a Pythonu. Místo křehkého P/Invoke (sd.cpp C ABI se mezi verzemi mění) voláme
/// CLI binárku se **stabilními argumenty** (viz <c>docs/native-generator-design.md §6</c>):
/// sestaví args (<see cref="SdCliArgsBuilder"/>), spustí proces, parsuje progres ze stdout/stderr
/// a posbírá výsledné PNG. Když <c>sd-cli</c> není přibalený, <see cref="Status"/> hlásí
/// „nedostupné" a UI fallbackne na ComfyUI.
/// </summary>
public sealed class NativeImageGenerator : INativeImageGenerator
{
    private static readonly Regex StepRegex = new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    private readonly string?       _outputDirOverride;
    private readonly string?       _cliPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string?          _modelPath;
    private NativeGenBackend _backend = NativeGenBackend.Cpu;

    public NativeImageGenerator(string? outputDirOverride = null, string? cliPathOverride = null)
    {
        _outputDirOverride = outputDirOverride;
        _cliPath           = ResolveCliPath(cliPathOverride);
    }

    public NativeGeneratorStatus Status => _cliPath is not null
        ? new(true, _backend, $"sd-cli ({Path.GetFileName(_cliPath)})")
        : new(false, NativeGenBackend.Cpu, "nedostupné",
              "Vestavěný generátor: nenašel jsem sd-cli.exe. Stáhni build stable-diffusion.cpp " +
              "(github.com/leejet/stable-diffusion.cpp) a dej sd-cli.exe vedle aplikace. " +
              "Zatím se generuje přes ComfyUI.");

    public bool IsModelLoaded => _modelPath is not null;

    public Task LoadModelAsync(string modelPath, NativeGenBackend backend, CancellationToken ct = default)
    {
        if (_cliPath is null)
            throw new InvalidOperationException("Vestavěný generátor není dostupný (chybí sd-cli.exe).");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new FileNotFoundException("Model nenalezen.", modelPath);

        // sd-cli nemá perzistentní kontext — model se předává při každé generaci. „Load" tu jen
        // zapamatuje výchozí model + backend (počet vláken pro CPU).
        _modelPath = modelPath;
        _backend   = backend;
        return Task.CompletedTask;
    }

    public async Task<NativeImageResult> GenerateAsync(
        NativeImageRequest request, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (request is null) return Fail("Chybí zadání.");
        if (_cliPath is null) return Fail(Status.UnavailableReason!);

        var model = !string.IsNullOrWhiteSpace(request.ModelPath) ? request.ModelPath : _modelPath;
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            return Fail("Model nenalezen: " + model);

        await _gate.WaitAsync(ct);
        try
        {
            var outDir = GetOutputDirectory();
            Directory.CreateDirectory(outDir);

            // Unikátní prefix → po běhu posbíráme všechny PNG (batch vytvoří víc souborů).
            var prefix  = $"AIStudio_native_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..46];
            var outPath = Path.Combine(outDir, prefix + ".png");
            var threads = _backend == NativeGenBackend.Cpu ? Math.Max(1, Environment.ProcessorCount) : 0;
            var args    = SdCliArgsBuilder.Build(request with { ModelPath = model! }, outPath, threads);

            Log.Information("sd-cli: {Cli} {Args}", _cliPath, string.Join(" ", args));
            progress?.Report(0);
            var (ok, tail) = await RunCliAsync(_cliPath, args, progress, ct);

            var produced = Directory.EnumerateFiles(outDir, prefix + "*.png")
                                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
            if (produced.Count == 0)
            {
                Log.Warning("sd-cli: žádný výstupní PNG (exit_ok={Ok}). výstup procesu:\n{Tail}", ok, Truncate(tail, 1500));
                return Fail(ok ? "sd-cli neprodukoval žádný obrázek (viz log)." : "sd-cli selhal: " + Truncate(tail, 400));
            }

            progress?.Report(100);
            Log.Information("NativeImageGenerator: hotovo ({N} obr.) přes {Cli}", produced.Count, Path.GetFileName(_cliPath));
            return new(true, produced);
        }
        catch (OperationCanceledException) { return Fail("Generování zrušeno."); }
        catch (Exception ex)
        {
            Log.Error(ex, "NativeImageGenerator(CLI): generování selhalo");
            return Fail("Generování selhalo: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    public Task UnloadAsync()
    {
        _modelPath = null;
        return Task.CompletedTask;
    }

    // ── Spuštění CLI + parsování progresu ──────────────────────────────────────

    private static async Task<(bool Ok, string Tail)> RunCliAsync(
        string cli, List<string> args, IProgress<int>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = cli,
            WorkingDirectory       = Path.GetDirectoryName(cli) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var tail = new StringBuilder();
        void Handle(string? line)
        {
            if (line is null) return;
            // Progres: sd-cli tiskne „<step>/<total>" během samplingu → mapuj na 5–95 %.
            var m = StepRegex.Match(line);
            if (m.Success
                && int.TryParse(m.Groups[1].Value, out var step)
                && int.TryParse(m.Groups[2].Value, out var total) && total > 0)
            {
                var pct = (int)Math.Clamp(5 + step / (double)total * 90, 5, 95);
                progress?.Report(pct);
            }
            lock (tail)
            {
                tail.AppendLine(line);
                if (tail.Length > 8000) tail.Remove(0, tail.Length - 8000);
            }
        }

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived  += (_, e) => Handle(e.Data);

        if (!proc.Start()) return (false, "sd-cli se nepodařilo spustit");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* už skončil */ }
            throw;
        }

        string captured;
        lock (tail) captured = tail.ToString();
        return (proc.ExitCode == 0, captured);
    }

    // ── Lokátor sd-cli ─────────────────────────────────────────────────────────

    /// <summary>
    /// Najde <c>sd-cli</c> binárku: override → vedle aplikace → <c>runtimes/&lt;rid&gt;/native/</c>
    /// → PATH. Vrací cestu nebo null. (Drop nové binárky za běhu vyžaduje restart appky.)
    /// </summary>
    private static string? ResolveCliPath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        // Jen „sd-cli" (název z aktuálního releasu). Bare „sd" by kolidovalo s jiným
        // nástrojem na PATH (sd = sed-alternativa) → falešná dostupnost.
        var names = OperatingSystem.IsWindows()
            ? new[] { "sd-cli.exe" }
            : new[] { "sd-cli" };

        var baseDir = AppContext.BaseDirectory;
        foreach (var n in names)
        {
            var p = Path.Combine(baseDir, n);
            if (File.Exists(p)) return p;
        }

        var rid = OperatingSystem.IsWindows() ? "win-x64"
                : OperatingSystem.IsMacOS()   ? "osx-arm64"
                : "linux-x64";
        foreach (var n in names)
        {
            var p = Path.Combine(baseDir, "runtimes", rid, "native", n);
            if (File.Exists(p)) return p;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var n in names)
            {
                try { var p = Path.Combine(dir.Trim(), n); if (File.Exists(p)) return p; }
                catch { /* neplatný PATH segment */ }
            }
        }
        return null;
    }

    private NativeImageResult Fail(string msg) => new(false, Array.Empty<string>(), msg);

    private string GetOutputDirectory() =>
        !string.IsNullOrEmpty(_outputDirOverride) ? _outputDirOverride : AppPaths.DefaultImagesDirectory;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
