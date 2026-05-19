using System.Diagnostics;
using System.Text.Json;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// macOS implementace <see cref="IGpuDetector"/>. Detekce probíhá přes
/// <c>system_profiler -json SPDisplaysDataType</c> — Apple toolchain command,
/// vrací JSON popis všech GPU adapterů (integrovaný + diskrétní).
///
/// **Důležité:** AI Studio cílí na macOS pouze pro Apple Silicon (M1/M2/M3/M4).
/// Intel Macy s diskrétní AMD kartou (např. MacBook Pro 16" 2019) nepodporujeme —
/// detektor vrátí <see cref="GpuVendor.Unknown"/> a aplikace propadne na CPU.
///
/// Unified memory: Apple Silicon nemá samostatnou VRAM. Celá GPU RAM = sdílená
/// systémová paměť, kterou Metal alokuje on-demand. Pro doporučení modelu (8B
/// vs 3B) se hodí číst <c>spdisplays_vram_shared</c> (např. "16 GB"), nebo
/// systém celkový RAM přes <c>sysctl hw.memsize</c>.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacOsGpuDetector : IGpuDetector
{
    public async Task<Gpu> DetectAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await RunSystemProfilerAsync(ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                Log.Information("MacOsGpuDetector: system_profiler vrátil prázdný výstup → Unknown");
                return Fallback();
            }

            var gpu = ParseAppleSilicon(json);
            if (gpu is not null)
            {
                Log.Information("MacOsGpuDetector: {Name} ({VramGb:F1} GB unified) → Metal",
                                gpu.Name, gpu.VramGb);
                return gpu;
            }

            Log.Information("MacOsGpuDetector: Apple Silicon nenalezen (možná Intel Mac) → Unknown/Cpu");
            return Fallback();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "MacOsGpuDetector: detekce selhala");
            return Fallback();
        }
    }

    private static Gpu Fallback() =>
        new(GpuVendor.Unknown, "Žádná podporovaná GPU", 0, GpuBackend.Cpu);

    // ── system_profiler invocation ────────────────────────────────────────────

    private static async Task<string> RunSystemProfilerAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "system_profiler",
            Arguments              = "-json SPDisplaysDataType",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return string.Empty;

        // Timeout 10 s — system_profiler může na slabších strojích chvíli trvat,
        // zejména první cold call po bootu.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            Log.Warning("MacOsGpuDetector: system_profiler skončilo s kódem {Code}", proc.ExitCode);
            return string.Empty;
        }
        return output;
    }

    // ── JSON parser ───────────────────────────────────────────────────────────

    /// <summary>
    /// Strukura odpovědi system_profileru:
    /// <code>
    /// {
    ///   "SPDisplaysDataType": [
    ///     {
    ///       "_name": "Apple M2 Pro",
    ///       "spdisplays_vendor": "Apple (0x106b)",
    ///       "spdisplays_vram_shared": "16 GB",
    ///       "spdisplays_metalfamily": "spdisplays_metal3",
    ///       ...
    ///     }
    ///   ]
    /// }
    /// </code>
    /// Vrátí <see cref="Gpu"/> pokud najde Apple Silicon záznam, jinak null.
    /// </summary>
    internal static Gpu? ParseAppleSilicon(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var entry in arr.EnumerateArray())
        {
            // Vendor — "Apple (0x106b)" je signál Apple Silicon SoC GPU
            var vendor = entry.TryGetProperty("spdisplays_vendor", out var v)
                ? v.GetString() ?? string.Empty
                : string.Empty;

            if (!IsAppleVendor(vendor)) continue;

            // Lidský název (např. "Apple M2 Pro")
            var name = entry.TryGetProperty("_name", out var n)
                ? n.GetString() ?? "Apple Silicon"
                : "Apple Silicon";

            // Unified memory — pro Apple Silicon je VRAM sdílená s RAM.
            // system_profiler udává "spdisplays_vram_shared" string jako "16 GB".
            // Některé builds používají dynamic allocation — pak hodnota chybí.
            long vramBytes = 0;
            if (entry.TryGetProperty("spdisplays_vram_shared", out var vramRaw))
            {
                vramBytes = ParseSizeString(vramRaw.GetString() ?? string.Empty);
            }

            return new Gpu(GpuVendor.Apple, name, vramBytes, GpuBackend.Metal);
        }

        return null;
    }

    /// <summary>True pokud vendor string identifikuje Apple Silicon SoC.</summary>
    internal static bool IsAppleVendor(string vendorString)
    {
        // Apple Silicon ID = 0x106B (Apple, Inc.). Některé starší system_profiler
        // verze vrací prostě "Apple" bez hex ID.
        var lower = vendorString.ToLowerInvariant();
        return lower.Contains("apple") || lower.Contains("0x106b");
    }

    /// <summary>
    /// Parsuje řetězce jako "16 GB", "8 GB", "1024 MB" na byty. Defenzivní
    /// — pokud formát neznáme, vrátí 0 (volající ukáže "neznámá VRAM").
    /// </summary>
    internal static long ParseSizeString(string size)
    {
        if (string.IsNullOrWhiteSpace(size)) return 0;
        var parts = size.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return 0;

        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                              System.Globalization.CultureInfo.InvariantCulture, out var value))
            return 0;

        var unit = parts[1].ToUpperInvariant();
        return unit switch
        {
            "B"  or "BYTES"     => (long)value,
            "KB" or "KIB"       => (long)(value * 1024),
            "MB" or "MIB"       => (long)(value * 1024 * 1024),
            "GB" or "GIB"       => (long)(value * 1024 * 1024 * 1024),
            "TB" or "TIB"       => (long)(value * 1024L * 1024 * 1024 * 1024),
            _                   => 0,
        };
    }
}
