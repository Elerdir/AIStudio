using System.Diagnostics;
using System.Management;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Windows implementace <see cref="IGpuDetector"/>. Dvoufázová detekce:
///
///   1) <c>nvidia-smi</c> probe — pokud projde, máme NVIDIA s přesnou VRAM.
///   2) WMI <c>Win32_VideoController</c> — fallback pro AMD/Intel.
///      Vendor se odvozuje z PNPDeviceID (PCI VEN_xxxx) nebo z názvu adapteru.
///
/// Výstup je <see cref="Gpu"/> s doporučeným backendem:
///   NVIDIA → Cuda, AMD/Intel → Vulkan, jinak → Cpu.
///
/// Pozn.: Třída cíleně nepouzívá <c>OperatingSystem.IsWindows()</c> guard
/// uvnitř public API — DI by ji neměl registrovat na non-Windows. Až přijde
/// macOS port, registrace se vyřeší přes platform-specific service registration.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsGpuDetector : IGpuDetector
{
    /// <summary>Kandidátní cesty pro nvidia-smi. PATH + nejčastější umístění.</summary>
    private static readonly string[] NvidiaSmiPaths =
    {
        "nvidia-smi",
        @"C:\Windows\System32\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
    };

    /// <summary>PCI vendor ID → výrobce. Hexa hodnoty z PCI-SIG registru.</summary>
    private static readonly Dictionary<string, GpuVendor> PciVendorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10DE"] = GpuVendor.Nvidia, // NVIDIA Corporation
        ["1002"] = GpuVendor.Amd,    // Advanced Micro Devices / ATI
        ["1022"] = GpuVendor.Amd,    // AMD CPU (iGPU APU)
        ["8086"] = GpuVendor.Intel,  // Intel Corporation
    };

    public async Task<Gpu> DetectAsync(CancellationToken ct = default)
    {
        // 1) NVIDIA cesta — pokud nvidia-smi projde, máme přesné údaje
        var nvidia = await TryDetectNvidiaAsync(ct);
        if (nvidia is not null) return nvidia;

        // 2) WMI fallback — libovolný vendor
        var wmi = TryDetectViaWmi();
        if (wmi is not null) return wmi;

        // 3) Nic — vrátíme Unknown/Cpu
        Log.Information("WindowsGpuDetector: žádná GPU nedetekována, fallback na CPU");
        return new Gpu(GpuVendor.Unknown, "Žádná GPU", 0, GpuBackend.Cpu);
    }

    // ── NVIDIA via nvidia-smi ─────────────────────────────────────────────────

    private static async Task<Gpu?> TryDetectNvidiaAsync(CancellationToken ct)
    {
        foreach (var path in NvidiaSmiPaths)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = path,
                    Arguments              = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

                using var proc = Process.Start(psi);
                if (proc is null) continue;

                var output = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);

                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) continue;

                // Format: "NVIDIA GeForce RTX 4070, 12282"
                var line  = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 2) continue;

                var name = parts[0];
                if (!long.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                                   System.Globalization.CultureInfo.InvariantCulture, out var totalMb))
                    continue;

                var vramBytes = totalMb * 1_048_576L;
                Log.Information("WindowsGpuDetector: NVIDIA {Name} ({Mb} MB) via nvidia-smi", name, totalMb);
                return new Gpu(GpuVendor.Nvidia, name, vramBytes, GpuBackend.Cuda);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Cesta selhala (nenalezena / přístup zamítnut) — zkus další
            }
        }
        return null;
    }

    // ── WMI fallback ──────────────────────────────────────────────────────────

    private static Gpu? TryDetectViaWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");

            Gpu? best = null;
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(name) || IsSoftwareAdapter(name)) continue;

                var pnpId   = obj["PNPDeviceID"]?.ToString() ?? string.Empty;
                var vendor  = ExtractVendor(pnpId, name);

                long vramBytes = 0;
                try
                {
                    // AdapterRAM je UInt32 — overflow nad 4 GB. Vrátíme 0 pro velké karty
                    // a UI sáhne pro VRAM přes runtime probe (DXGI / nvml).
                    var ramRaw = obj["AdapterRAM"];
                    if (ramRaw != null)
                    {
                        var ram = Convert.ToInt64(ramRaw);
                        vramBytes = ram > 0 ? ram : 0;
                    }
                }
                catch { /* AdapterRAM nedostupné — VRAM zůstane 0 */ }

                var backend = ChooseBackend(vendor);
                var gpu     = new Gpu(vendor, name, vramBytes, backend);

                // Mezi více adaptery preferujeme:
                //   1) Vyšší prioritu vendoru (NVIDIA > AMD > Intel)
                //   2) Při stejné prioritě vyšší VRAM
                if (best is null || VendorPriority(gpu.Vendor) > VendorPriority(best.Vendor)
                                 || (VendorPriority(gpu.Vendor) == VendorPriority(best.Vendor)
                                     && gpu.VramBytes > best.VramBytes))
                {
                    best = gpu;
                }
            }

            if (best is not null)
            {
                Log.Information("WindowsGpuDetector: WMI = {Vendor} {Name} ({VramGb:F1} GB, backend={Backend})",
                                best.Vendor, best.Name, best.VramGb, best.Backend);
                return best;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WindowsGpuDetector: WMI enumerace selhala");
        }

        return null;
    }

    // ── Helpers (internal pro testy) ─────────────────────────────────────────

    /// <summary>
    /// Vyhodí adaptery, které nejsou skutečné fyzické GPU
    /// (Microsoft Basic Display, RDP, virtuální IDDs apod.).
    /// </summary>
    internal static bool IsSoftwareAdapter(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("microsoft basic display")
            || lower.Contains("microsoft remote display")
            || lower.Contains("remote desktop")
            || lower.Contains("idd")
            || lower.Contains("meta virtual")
            || lower.Contains("parsec virtual")
            || lower.Contains("displaylink");
    }

    /// <summary>
    /// Extrahuje vendora z PCI PNPDeviceID (formát "PCI\VEN_10DE&amp;DEV_xxxx&amp;…")
    /// nebo z lidského názvu jako fallback ("AMD Radeon", "NVIDIA GeForce", "Intel UHD").
    /// </summary>
    internal static GpuVendor ExtractVendor(string pnpId, string name)
    {
        // Primární: PCI vendor ID
        var venIdx = pnpId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (venIdx >= 0 && pnpId.Length >= venIdx + 8)
        {
            var venId = pnpId.Substring(venIdx + 4, 4);
            if (PciVendorIds.TryGetValue(venId, out var v)) return v;
        }

        // Fallback: keyword v názvu
        var lower = name.ToLowerInvariant();
        if (lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("quadro") || lower.Contains("tesla"))
            return GpuVendor.Nvidia;
        if (lower.Contains("amd")    || lower.Contains("radeon")  || lower.Contains("ati "))
            return GpuVendor.Amd;
        if (lower.Contains("intel")  || lower.Contains("iris")    || lower.Contains("arc "))
            return GpuVendor.Intel;

        return GpuVendor.Unknown;
    }

    /// <summary>Mapování vendor → výchozí backend.</summary>
    internal static GpuBackend ChooseBackend(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => GpuBackend.Cuda,
        GpuVendor.Amd    => GpuBackend.Vulkan,
        GpuVendor.Intel  => GpuBackend.Vulkan,
        // Apple Silicon detekce přijde s macOS portem
        _                => GpuBackend.Cpu,
    };

    /// <summary>
    /// Priorita pro výběr mezi více detekovanými GPU. Vyšší = preferováno.
    /// Diskrétní vendor (NVIDIA/AMD) > Intel iGPU > Unknown.
    /// </summary>
    private static int VendorPriority(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia  => 3,
        GpuVendor.Amd     => 3,
        GpuVendor.Apple   => 3,
        GpuVendor.Intel   => 2,
        _                 => 1,
    };
}
