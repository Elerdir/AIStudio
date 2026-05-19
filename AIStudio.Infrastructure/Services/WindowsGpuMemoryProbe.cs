using LibreHardwareMonitor.Hardware;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Stateless wrapper kolem LibreHardwareMonitorLib pro načtení VRAM
/// statistik nezávisle na vendoru. Řeší klasický problém WMI Win32_VideoController:
///
///   "AdapterRAM" je <c>UInt32</c> v bytech → přetéká nad 4 GB. Pro RTX 4070
///   (12 GB) WMI vrací <c>0</c>, pro RX 6750 (12 GB) totéž. LHM používá
///   vendor-specifické API (NVAPI, ADL, D3D registry), které UInt32 problém
///   nemají.
///
/// **Náklady:** každé volání <see cref="TryReadVram"/> vytvoří <c>Computer</c>
/// instanci (~50-100 ms cold init) a okamžitě ji zavře. Pro one-shot detekci
/// v <see cref="WindowsGpuDetector"/> to stačí; pro live monitoring (B.4.2)
/// budeme držet dlouho-žijící singleton.
///
/// **Admin práva:** total VRAM size je metadata sensor (čteno z PCI / D3D
/// registry), funguje bez admin. Used VRAM někdy vyžaduje elevation —
/// v B.4.2 to ošetříme samostatně.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsGpuMemoryProbe
{
    /// <summary>
    /// Najde první diskrétní GPU (NVIDIA / AMD / Intel) a vrátí její celkovou
    /// VRAM v bytech. Pokud nic nenajde nebo LHM selže, vrátí 0.
    /// </summary>
    public static long TryReadVramTotalBytes()
    {
        // LHM Computer nemá IDisposable — používáme Open/Close pár v try/finally.
        var computer = new Computer { IsGpuEnabled = true };
        try
        {
            computer.Open();

            foreach (var hardware in computer.Hardware)
            {
                if (!IsGpuHardware(hardware.HardwareType)) continue;
                hardware.Update();

                // LHM má dva typické názvy pro total VRAM:
                //   - "GPU Memory Total" (NVIDIA, novější AMD)
                //   - "D3D Dedicated Memory Total" (Intel, starší AMD via D3D11)
                // SensorType.SmallData = velikost v MB.
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.SmallData) continue;
                    var name = sensor.Name ?? string.Empty;
                    if (!IsTotalVramSensor(name)) continue;
                    if (sensor.Value is not float mbValue || mbValue <= 0) continue;

                    var bytes = (long)(mbValue * 1024 * 1024);
                    Log.Debug("WindowsGpuMemoryProbe: {Hw} {Sensor} = {Mb} MB",
                              hardware.Name, name, mbValue);
                    return bytes;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WindowsGpuMemoryProbe: čtení VRAM přes LHM selhalo");
        }
        finally
        {
            try { computer.Close(); } catch { /* best effort cleanup */ }
        }

        return 0;
    }

    /// <summary>True pro diskrétní + integrované GPU (NVIDIA, AMD, Intel).</summary>
    internal static bool IsGpuHardware(HardwareType type) => type is
        HardwareType.GpuNvidia or
        HardwareType.GpuAmd    or
        HardwareType.GpuIntel;

    /// <summary>
    /// True pokud název sensoru odpovídá celkové VRAM kapacitě. LHM používá
    /// několik variant názvů podle vendora — zachytíme všechny do jednoho předikátu.
    /// </summary>
    internal static bool IsTotalVramSensor(string sensorName)
    {
        var lower = sensorName.ToLowerInvariant();
        // "GPU Memory Total", "D3D Dedicated Memory Total" — všechny obsahují
        // "memory total" jako substring. Vyloučí "GPU Memory Used", "Free" apod.
        return lower.Contains("memory total");
    }
}
