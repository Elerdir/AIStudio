using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

public sealed class SystemMonitorService : ISystemMonitorService, IDisposable
{
    private CancellationTokenSource? _cts;
    private readonly int _currentPid = Environment.ProcessId;

    // Statické věci se zjišťují jen jednou — WMI dotazy jsou drahé
    private string? _cachedCpuName;

    // Snapshot pro výpočet CPU usage přes GetSystemTimes (deltami mezi voláními).
    // Ulož při prvním volání, pak rozdíl k aktuálním hodnotám = % využití.
    private long _prevIdleTicks;
    private long _prevKernelTicks;
    private long _prevUserTicks;

    // Kandidátní cesty pro nvidia-smi — PATH + nejčastější umístění na Windows
    private static readonly string[] NvidiaSmiCandidates =
    [
        "nvidia-smi",
        @"C:\Windows\System32\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
    ];

    public SystemStatus Current { get; private set; } = new();
    public event EventHandler<SystemStatus>? StatusUpdated;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // CPU usage měříme přes Win32 GetSystemTimes — žádný blokující init, narozdíl
        // od PerformanceCounter, který na čistém Windows umí na několik desítek vteřin
        // viset v konstruktoru (enumerace registry counters), nebo se i úplně zaseknout.
        return Task.Run(async () =>
        {
            Log.Information("SystemMonitor: starting collection loop");

            // Inicializace baseline pro CPU delta výpočet — první GetCpuUsage()
            // tedy vrátí 0, druhé už reálné %.
            ReadSystemTimes(out _prevIdleTicks, out _prevKernelTicks, out _prevUserTicks);

            // CPU název zjistíme jednou na začátku (WMI dotaz je pomalý)
            _cachedCpuName = GetCpuName();
            Log.Information("SystemMonitor: CPU = {Cpu}", _cachedCpuName);

            // První collect provedeme hned, ať uživatel nečeká celých 2,5 s na zobrazení dat
            try
            {
                Current = await CollectAsync();
                StatusUpdated?.Invoke(this, Current);
                Log.Information("SystemMonitor: initial snapshot — CPU={Cpu}%, RAM={RamU:F1}/{RamT:F0}GB, GPU={Gpu} (avail={Avail})",
                    Current.CpuUsagePercent, Current.RamUsedGb, Current.RamTotalGb,
                    Current.GpuName, Current.GpuAvailable);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SystemMonitor: initial collect failed");
            }

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    Current = await CollectAsync();
                    StatusUpdated?.Invoke(this, Current);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "SystemMonitor: CollectAsync iteration failed");
                }

                try   { await Task.Delay(2500, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }

            Log.Information("SystemMonitor: collection loop stopped");
        }, _cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    private async Task<SystemStatus> CollectAsync()
    {
        var cpuTask = Task.Run(GetCpuUsage);
        var ramTask = Task.Run(GetRamInfo);
        var gpuTask = Task.Run(GetGpuInfo);

        await Task.WhenAll(cpuTask, ramTask, gpuTask);

        var (ramUsed, ramTotal) = ramTask.Result;
        var gpu = gpuTask.Result;

        return new SystemStatus
        {
            CpuName               = _cachedCpuName ?? string.Empty,
            CpuUsagePercent       = cpuTask.Result,
            RamUsedGb             = ramUsed,
            RamTotalGb            = ramTotal,
            GpuName               = gpu.Name,
            VramUsedGb            = gpu.VramUsedGb,
            VramTotalGb           = gpu.VramTotalGb,
            GpuUtilizationPercent = gpu.UtilizationPercent,
            GpuAvailable          = gpu.Available,
            GpuProcesses          = gpu.Processes
        };
    }

    // ── CPU name (WMI Win32_Processor) ───────────────────────────────────────

    private static string GetCpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            // 1) WMI Win32_Processor — nejhezčí název ("Intel Core i7-12700K …")
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        Log.Information("SystemMonitor: GetCpuName WMI = {Name}", name);
                        return name;
                    }
                }
                Log.Warning("SystemMonitor: WMI Win32_Processor.Name vrátilo prázdný / žádný záznam");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SystemMonitor: GetCpuName WMI failed");
            }

            // 2) Registry HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0\\ProcessorNameString
            //    Funguje i když System.Management WMI selže (občasná Win11 / .NET regrese).
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = key?.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    Log.Information("SystemMonitor: GetCpuName Registry = {Name}", name.Trim());
                    return name.Trim();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SystemMonitor: GetCpuName Registry failed");
            }
        }

        // 3) Env var PROCESSOR_IDENTIFIER — méně hezké ("Intel64 Family 6 Model 158 …"), ale cross-platform
        var fromEnv = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")?.Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(fromEnv))
        {
            Log.Information("SystemMonitor: GetCpuName ENV = {Name}", fromEnv);
            return fromEnv;
        }

        Log.Warning("SystemMonitor: žádný zdroj CPU názvu nedostupný — používám 'Neznámý procesor'");
        return "Neznámý procesor";
    }

    // ── CPU usage ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Spočítá průměrné CPU využití od posledního volání pomocí Win32
    /// <c>GetSystemTimes</c>. Princip: Windows udržuje od bootu kumulativní
    /// počet 100ns ticků v idle/kernel/user režimu napříč všemi jádry.
    /// Rozdíl mezi dvěma snapshoty dá podíl idle vs. busy ticků = CPU usage %.
    ///
    /// První volání po startu vrátí 0 (chybí baseline), druhé už dá reálnou hodnotu.
    /// </summary>
    private double GetCpuUsage()
    {
        // Win32 GetSystemTimes funguje jen na Windows. Na macOS / Linux teď
        // vrátíme 0 — proper detekce přes host_processor_info / /proc/stat
        // přijde v Phase 2 systém monitoringu pro Apple Silicon.
        if (!OperatingSystem.IsWindows())
            return 0;

        try
        {
            if (!ReadSystemTimes(out var idleNow, out var kernelNow, out var userNow))
                return 0;

            var idleDelta   = idleNow   - _prevIdleTicks;
            var kernelDelta = kernelNow - _prevKernelTicks;
            var userDelta   = userNow   - _prevUserTicks;

            _prevIdleTicks   = idleNow;
            _prevKernelTicks = kernelNow;
            _prevUserTicks   = userNow;

            // kernel time zahrnuje idle time → busy = (kernel + user) − idle
            var totalDelta = kernelDelta + userDelta;
            if (totalDelta <= 0) return 0;

            var busy = totalDelta - idleDelta;
            return Math.Round(100.0 * busy / totalDelta, 1);
        }
        catch
        {
            return 0;
        }
    }

    private static bool ReadSystemTimes(out long idle, out long kernel, out long user)
    {
        idle = kernel = user = 0;
        if (!OperatingSystem.IsWindows()) return false;

        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return false;

        idle   = ((long)idleFt.dwHighDateTime   << 32) | (uint)idleFt.dwLowDateTime;
        kernel = ((long)kernelFt.dwHighDateTime << 32) | (uint)kernelFt.dwLowDateTime;
        user   = ((long)userFt.dwHighDateTime   << 32) | (uint)userFt.dwLowDateTime;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool GetSystemTimes(
        out FileTimeStruct idleTime, out FileTimeStruct kernelTime, out FileTimeStruct userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimeStruct
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    // ── RAM ────────────────────────────────────────────────────────────────────

    private static bool _ramLoggedOnce;

    private static (double Used, double Total) GetRamInfo()
    {
        if (!OperatingSystem.IsWindows())
            return (0, 0);

        try
        {
            var mem = new MemoryStatusEx();
            mem.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            if (!GlobalMemoryStatusEx(ref mem))
            {
                if (!_ramLoggedOnce)
                {
                    var err = Marshal.GetLastWin32Error();
                    Log.Warning("SystemMonitor: GlobalMemoryStatusEx vrátilo false (Win32 err {Err})", err);
                    _ramLoggedOnce = true;
                }
                return (0, 0);
            }

            double total = mem.TotalPhys / 1024.0 / 1024.0 / 1024.0;
            double avail = mem.AvailPhys / 1024.0 / 1024.0 / 1024.0;
            var used  = Math.Round(total - avail, 1);
            var totalRounded = Math.Round(total, 1);

            if (!_ramLoggedOnce)
            {
                Log.Information("SystemMonitor: RAM = {Used:F1} / {Total:F1} GB (used / total)",
                                used, totalRounded);
                _ramLoggedOnce = true;
            }

            return (used, totalRounded);
        }
        catch (Exception ex)
        {
            if (!_ramLoggedOnce)
            {
                Log.Warning(ex, "SystemMonitor: GetRamInfo selhalo");
                _ramLoggedOnce = true;
            }
            return (0, 0);
        }
    }

    // ── GPU — NVIDIA (nvidia-smi) + WMI fallback ──────────────────────────────

    // Logujeme výsledek detekce GPU jen jednou — jinak by 1× za 2,5 s ucpávalo log
    private bool _gpuLoggedOnce;

    private (string Name, double VramUsedGb, double VramTotalGb,
             double UtilizationPercent, bool Available, IReadOnlyList<GpuProcess> Processes)
        GetGpuInfo()
    {
        // ── Pokus 1: nvidia-smi (NVIDIA) ─────────────────────────────────────
        try
        {
            var gpuOutput = RunNvidiaSmi(
                "--query-gpu=name,memory.used,memory.total,utilization.gpu",
                "--format=csv,noheader,nounits");

            if (!string.IsNullOrWhiteSpace(gpuOutput))
            {
                var parts = gpuOutput.Trim().Split(',');
                if (parts.Length >= 4)
                {
                    var name = parts[0].Trim();
                    double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var vramUsedMb);
                    double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var vramTotalMb);
                    double.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out var utilization);

                    var procOutput = RunNvidiaSmi(
                        "--query-compute-apps=pid,used_memory",
                        "--format=csv,noheader,nounits");

                    if (!_gpuLoggedOnce)
                    {
                        Log.Information("SystemMonitor: GPU via nvidia-smi = {Name} ({TotalGb:F1} GB)",
                                        name, vramTotalMb / 1024.0);
                        _gpuLoggedOnce = true;
                    }

                    return (
                        name,
                        Math.Round(vramUsedMb  / 1024.0, 1),
                        Math.Round(vramTotalMb / 1024.0, 1),
                        utilization,
                        true,
                        ParseGpuProcesses(procOutput ?? "")
                    );
                }
            }
            else if (!_gpuLoggedOnce)
            {
                Log.Information("SystemMonitor: nvidia-smi nedostupné nebo bez výstupu — zkouším WMI");
            }
        }
        catch (Exception ex)
        {
            if (!_gpuLoggedOnce)
                Log.Warning(ex, "SystemMonitor: nvidia-smi cesta selhala — zkouším WMI");
        }

        // ── Pokus 2: WMI Win32_VideoController (libovolná GPU) ───────────────
        if (!OperatingSystem.IsWindows())
            return ("N/A", 0, 0, 0, false, []);

        try
        {
            var (wmiName, wmiVramGb) = GetWmiGpuBasicInfo();
            if (!string.IsNullOrEmpty(wmiName))
            {
                if (!_gpuLoggedOnce)
                {
                    Log.Information("SystemMonitor: GPU via WMI = {Name} ({TotalGb:F1} GB)",
                                    wmiName, wmiVramGb);
                    _gpuLoggedOnce = true;
                }
                return (wmiName, 0, wmiVramGb, 0, true, []);
            }

            if (!_gpuLoggedOnce)
            {
                Log.Warning("SystemMonitor: WMI Win32_VideoController nevrátilo žádnou GPU");
                _gpuLoggedOnce = true;
            }
        }
        catch (Exception ex)
        {
            if (!_gpuLoggedOnce)
            {
                Log.Warning(ex, "SystemMonitor: WMI GPU detekce selhala");
                _gpuLoggedOnce = true;
            }
        }

        return ("N/A", 0, 0, 0, false, []);
    }

    // ── nvidia-smi helper — zkouší všechny kandidátní cesty ──────────────────

    private static string? RunNvidiaSmi(params string[] args)
    {
        foreach (var path in NvidiaSmiCandidates)
        {
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName               = path,
                        Arguments              = string.Join(" ", args),
                        RedirectStandardOutput = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true
                    }
                };
                proc.Start();
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(output))
                    return output;
            }
            catch { /* zkusíme další cestu */ }
        }
        return null;
    }

    // ── WMI fallback — libovolná GPU (název + celková VRAM) ──────────────────

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string Name, double VramTotalGb) GetWmiGpuBasicInfo()
    {
        // Necháváme bez WHERE filteru — některé systémy nedávají do PNPDeviceID prefix "PCI",
        // u VM třeba "ROOT\…". Místo toho projdeme všechny adaptery a softwarové
        // renderery (Microsoft Basic Display, RDP) vyhodíme až v post-filteru.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;

                // Vynech zjevně softwarové displeje
                var lower = name.ToLowerInvariant();
                if (lower.Contains("microsoft basic display") ||
                    lower.Contains("remote desktop") ||
                    lower.Contains("idd") ||                  // virtuální displeje (Parsec apod.)
                    lower.Contains("meta virtual"))
                    continue;

                // AdapterRAM je UInt32 — přesné jen do ~4 GB (overflow u >4 GB karet).
                // Pro NVIDIA používáme nvidia-smi; tady je to best-effort fallback pro AMD/Intel.
                double vramGb = 0;
                try
                {
                    var ramRaw = obj["AdapterRAM"];
                    if (ramRaw != null)
                        vramGb = Math.Round(Convert.ToDouble(ramRaw) / 1024 / 1024 / 1024, 1);
                }
                catch { }

                Log.Debug("SystemMonitor: WMI GPU = {Name} ({VramGb} GB)", name, vramGb);
                return (name, vramGb);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SystemMonitor: WMI GPU enumeration failed");
        }

        return (string.Empty, 0);
    }

    // ── Parsování procesů z nvidia-smi ───────────────────────────────────────

    private List<GpuProcess> ParseGpuProcesses(string output)
    {
        var result = new List<GpuProcess>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[0].Trim(), out var pid)) continue;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                                  System.Globalization.CultureInfo.InvariantCulture, out var vramMb)) continue;

            string procName;
            try { procName = Process.GetProcessById(pid).ProcessName; }
            catch { procName = $"PID {pid}"; }

            result.Add(new GpuProcess
            {
                Pid          = pid,
                Name         = procName,
                VramUsedMb   = vramMb,
                IsCurrentApp = pid == _currentPid
            });
        }
        return result;
    }

    // ── Win32 RAM ─────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint  Length;
        public uint  MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    public void Dispose()
    {
        _cts?.Cancel();
    }
}
