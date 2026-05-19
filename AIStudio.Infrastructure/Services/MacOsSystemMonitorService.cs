using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// macOS implementace <see cref="ISystemMonitorService"/>. Sbírá CPU/RAM/GPU
/// metriky pomocí Unix command-line nástrojů, které jsou součástí každé
/// instalace macOS od 10.0:
///
///   • <c>sysctl -n hw.memsize</c>     — total RAM v bytech
///   • <c>vm_stat</c>                  — page-grained memory breakdown
///                                       (active, wired, compressed, free)
///   • <c>top -l 1 -s 0</c>            — CPU usage (user + sys + idle %)
///   • <c>system_profiler -json …</c>  — GPU jméno + unified memory
///
/// **Apple Silicon (M1/M2/M3/M4) má unified memory** — RAM a VRAM sdílejí
/// jednu fyzickou paměť. Pro UI display rozlišujeme:
///   - RamTotalGb / RamUsedGb = total physical memory + (active+wired+compressed)
///   - VramTotalGb            = stejná total memory (unified)
///   - VramUsedGb             = "wired" memory (lock pro GPU buffers + kernel)
///
/// **Pozor:** wired memory není přesný GPU pressure indicator — Metal alokace
/// se počítají jako wired, ale wired obsahuje i kernel data. Pro lepší metric
/// by bylo potřeba <c>powermetrics</c> (vyžaduje sudo) nebo IOKit P/Invoke.
/// Pro UI hlášku "VRAM 8 GB / 16 GB" wired memory stačí — je v řádech přesná.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacOsSystemMonitorService : ISystemMonitorService, IDisposable
{
    private CancellationTokenSource? _cts;
    private string? _cachedCpuName;
    private string? _cachedGpuName;
    private long    _cachedTotalRamBytes;

    public SystemStatus Current { get; private set; } = new();
    public event EventHandler<SystemStatus>? StatusUpdated;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return Task.Run(async () =>
        {
            Log.Information("MacOsSystemMonitor: starting collection loop");

            // Statické hodnoty se zjišťují jen jednou
            _cachedCpuName        = await ReadSysctlStringAsync("machdep.cpu.brand_string", _cts.Token);
            _cachedTotalRamBytes  = await ReadSysctlLongAsync("hw.memsize", _cts.Token);
            _cachedGpuName        = await ReadGpuNameAsync(_cts.Token);

            Log.Information("MacOsSystemMonitor: CPU={Cpu}, GPU={Gpu}, RAM={RamGb:F0} GB",
                            _cachedCpuName, _cachedGpuName, _cachedTotalRamBytes / 1_073_741_824.0);

            // První snapshot ihned, pak smyčka 2.5 s
            try
            {
                Current = await CollectAsync(_cts.Token);
                StatusUpdated?.Invoke(this, Current);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MacOsSystemMonitor: initial collect failed");
            }

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    Current = await CollectAsync(_cts.Token);
                    StatusUpdated?.Invoke(this, Current);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "MacOsSystemMonitor: CollectAsync iteration failed");
                }

                try   { await Task.Delay(2500, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }

            Log.Information("MacOsSystemMonitor: collection loop stopped");
        }, _cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    public void Dispose() => _cts?.Dispose();

    // ── Sběr metrik ──────────────────────────────────────────────────────────

    private async Task<SystemStatus> CollectAsync(CancellationToken ct)
    {
        var cpuTask = ReadCpuUsageAsync(ct);
        var ramTask = ReadRamUsageAsync(ct);

        await Task.WhenAll(cpuTask, ramTask);

        var (ramUsedGb, ramTotalGb, wiredGb) = ramTask.Result;

        return new SystemStatus
        {
            CpuName               = _cachedCpuName ?? string.Empty,
            CpuUsagePercent       = cpuTask.Result,
            RamUsedGb             = ramUsedGb,
            RamTotalGb            = ramTotalGb,
            GpuName               = _cachedGpuName ?? string.Empty,
            // Unified memory: VRAM totalt = RAM total, "used" = wired pages
            // (alokované pro GPU buffers + kernel). Není to přesné Metal-only,
            // ale aproximaci do UI dashboardu to splní.
            VramTotalGb           = ramTotalGb,
            VramUsedGb            = wiredGb,
            GpuUtilizationPercent = 0, // bez powermetrics neumíme změřit
            GpuAvailable          = !string.IsNullOrEmpty(_cachedGpuName),
            GpuProcesses          = Array.Empty<GpuProcess>(),
        };
    }

    // ── sysctl helpers ──────────────────────────────────────────────────────

    private static async Task<string> ReadSysctlStringAsync(string key, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("sysctl", $"-n {key}", ct);
            return output.Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MacOsSystemMonitor: sysctl {Key} selhalo", key);
            return string.Empty;
        }
    }

    private static async Task<long> ReadSysctlLongAsync(string key, CancellationToken ct)
    {
        var s = await ReadSysctlStringAsync(key, ct);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    // ── CPU usage přes `top -l 1 -s 0` ───────────────────────────────────────

    /// <summary>
    /// `top -l 1 -s 0 -n 0` vrací jeden snapshot bez procesů. Klíčový řádek:
    /// <code>CPU usage: 12.34% user, 5.67% sys, 81.99% idle</code>
    /// Vrací (100 - idle) = celkové aktivní vytížení.
    /// </summary>
    private static async Task<double> ReadCpuUsageAsync(CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("top", "-l 1 -s 0 -n 0", ct);
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("CPU usage:", StringComparison.Ordinal)) continue;

                // "CPU usage: 12.34% user, 5.67% sys, 81.99% idle"
                var idleIdx = line.IndexOf("idle", StringComparison.Ordinal);
                if (idleIdx <= 0) continue;
                // Najdi poslední "%" před "idle"
                var pctIdx = line.LastIndexOf('%', idleIdx);
                if (pctIdx <= 0) continue;
                var numStart = line.LastIndexOf(' ', pctIdx);
                if (numStart < 0) continue;

                var numStr = line.Substring(numStart + 1, pctIdx - numStart - 1);
                if (double.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var idle))
                    return Math.Round(100.0 - idle, 1);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "MacOsSystemMonitor: top -l 1 selhalo");
        }
        return 0;
    }

    // ── RAM usage přes `vm_stat` ─────────────────────────────────────────────

    /// <summary>
    /// `vm_stat` vrátí stránkový breakdown paměti. Klíčové řádky:
    /// <code>
    /// Pages free:               123456.
    /// Pages active:             234567.
    /// Pages inactive:           345678.
    /// Pages speculative:         12345.
    /// Pages wired down:          54321.
    /// Pages occupied by compressor: 89012.
    /// </code>
    /// Used = (active + wired + compressed) × page_size
    /// Tuple: (usedGb, totalGb, wiredGb)
    /// </summary>
    private async Task<(double usedGb, double totalGb, double wiredGb)> ReadRamUsageAsync(CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("vm_stat", string.Empty, ct);
            long pageSize = 4096; // macOS default; vm_stat header obsahuje "page size of N bytes"
            long active = 0, wired = 0, compressed = 0;

            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Mach Virtual Memory Statistics", StringComparison.Ordinal))
                {
                    // "(page size of 16384 bytes)" — Apple Silicon používá 16K pages
                    var idx = line.IndexOf("page size of ", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var after = line.Substring(idx + "page size of ".Length);
                        var spaceIdx = after.IndexOf(' ');
                        if (spaceIdx > 0
                            && long.TryParse(after[..spaceIdx], NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var p))
                            pageSize = p;
                    }
                }
                else if (line.StartsWith("Pages active:", StringComparison.Ordinal))
                    active = ParsePagesLine(line);
                else if (line.StartsWith("Pages wired down:", StringComparison.Ordinal))
                    wired = ParsePagesLine(line);
                else if (line.StartsWith("Pages occupied by compressor", StringComparison.Ordinal))
                    compressed = ParsePagesLine(line);
            }

            var usedBytes  = (active + wired + compressed) * pageSize;
            var wiredBytes = wired * pageSize;
            var totalBytes = _cachedTotalRamBytes > 0 ? _cachedTotalRamBytes : usedBytes;

            return (
                Math.Round(usedBytes  / 1_073_741_824.0, 2),
                Math.Round(totalBytes / 1_073_741_824.0, 1),
                Math.Round(wiredBytes / 1_073_741_824.0, 2));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "MacOsSystemMonitor: vm_stat selhalo");
            return (0, _cachedTotalRamBytes / 1_073_741_824.0, 0);
        }
    }

    /// <summary>Extrahuje číslo z řádku jako "Pages active:                  234567."</summary>
    private static long ParsePagesLine(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return 0;
        var num = line.Substring(colon + 1).Trim().TrimEnd('.');
        return long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    // ── GPU name (z system_profileru, jen jednou při startu) ────────────────

    private static async Task<string> ReadGpuNameAsync(CancellationToken ct)
    {
        try
        {
            var json = await RunCommandAsync("system_profiler", "-json SPDisplaysDataType", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var arr)) return string.Empty;

            foreach (var entry in arr.EnumerateArray())
            {
                if (entry.TryGetProperty("_name", out var n) && n.GetString() is { Length: > 0 } name)
                    return name;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "MacOsSystemMonitor: system_profiler selhalo");
        }
        return string.Empty;
    }

    // ── Generic process runner ──────────────────────────────────────────────

    private static async Task<string> RunCommandAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null) return string.Empty;

        // 5 s timeout pro běžné metriky (sysctl, vm_stat). system_profiler může
        // při prvním cold call trvat déle — uživatel volá ReadGpuNameAsync jen
        // jednou na začátku, kde to akceptujeme.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { } throw; }

        return output;
    }
}
