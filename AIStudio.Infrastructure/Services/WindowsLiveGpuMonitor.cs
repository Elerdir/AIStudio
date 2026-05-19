using LibreHardwareMonitor.Hardware;
using Serilog;

namespace AIStudio.Infrastructure.Services;

/// <summary>
/// Dlouho-žijící LibreHardwareMonitor wrapper pro polling VRAM Used / Total
/// na AMD a Intel kartách. Volá <see cref="WindowsSystemMonitorService"/>
/// každé 2,5 s — proto držíme jednu <c>Computer</c> instanci a jen voláme
/// <c>hardware.Update()</c> v cyklu (LHM tu operaci dělá v řádech ms,
/// na rozdíl od cold Open() který trvá ~50–100 ms).
///
/// **Rozdíl od <see cref="WindowsGpuMemoryProbe"/>:** ten je stateless
/// jednorázový probe pro detekci. Tento monitor je statefull singleton
/// pro live polling. Oba mohou existovat vedle sebe — detektor se spustí
/// jednou při startu, monitor pak průběžně sleduje.
///
/// **Lifecycle:** volající MUSÍ zavolat <see cref="Initialize"/> před prvním
/// <see cref="TryReadCurrent"/> a <see cref="Dispose"/> při shutdown
/// (jinak LHM ProcessHooks zůstanou v paměti).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsLiveGpuMonitor : IDisposable
{
    private Computer? _computer;
    private IHardware? _gpuHardware;     // cache najít poprvé, pak jen update()
    private bool _initialized;
    private bool _disposed;

    /// <summary>True pokud máme funkční LHM instanci a GPU hardware záznam.</summary>
    public bool IsAvailable => _initialized && _gpuHardware is not null && !_disposed;

    /// <summary>
    /// Inicializuje <see cref="Computer"/> a najde první diskrétní GPU.
    /// Idempotentní — opakované volání je no-op.
    /// </summary>
    public void Initialize()
    {
        if (_initialized || _disposed) return;

        try
        {
            _computer = new Computer { IsGpuEnabled = true };
            _computer.Open();

            foreach (var hw in _computer.Hardware)
            {
                if (!WindowsGpuMemoryProbe.IsGpuHardware(hw.HardwareType)) continue;
                _gpuHardware = hw;
                Log.Information("WindowsLiveGpuMonitor: navázán na {Hw} ({Type})",
                                hw.Name, hw.HardwareType);
                break;
            }

            if (_gpuHardware is null)
                Log.Information("WindowsLiveGpuMonitor: LHM otevřen, ale žádná GPU hardware nenalezena");

            _initialized = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WindowsLiveGpuMonitor: Initialize selhalo, monitoring nebude dostupný");
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }

    /// <summary>
    /// Načte aktuální VRAM used + total v GB. Vrátí (0, 0) pokud LHM nebyl
    /// inicializován nebo neumí daný senzor přečíst.
    /// </summary>
    public (double UsedGb, double TotalGb) TryReadCurrent()
    {
        if (!IsAvailable || _gpuHardware is null) return (0, 0);

        try
        {
            _gpuHardware.Update();

            double usedMb  = 0;
            double totalMb = 0;

            foreach (var sensor in _gpuHardware.Sensors)
            {
                if (sensor.SensorType != SensorType.SmallData) continue;
                if (sensor.Value is not float mb || mb <= 0) continue;

                var name = sensor.Name ?? string.Empty;
                if (WindowsGpuMemoryProbe.IsTotalVramSensor(name))
                {
                    totalMb = mb;
                }
                else if (IsUsedVramSensor(name))
                {
                    // Uchováme nejvyšší hodnotu — Intel D3D vrací několik
                    // měrítek (dedicated + shared), bereme to největší jako proxy.
                    if (mb > usedMb) usedMb = mb;
                }
            }

            return (
                Math.Round(usedMb  / 1024.0, 2),
                Math.Round(totalMb / 1024.0, 1));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WindowsLiveGpuMonitor: TryReadCurrent selhalo");
            return (0, 0);
        }
    }

    /// <summary>
    /// True pokud název sensoru odpovídá použité VRAM. Pokrývá:
    /// "GPU Memory Used" (NVIDIA/AMD), "D3D Dedicated Memory Used" (Intel/AMD),
    /// "D3D Shared Memory Used".
    /// </summary>
    internal static bool IsUsedVramSensor(string sensorName)
    {
        var lower = sensorName.ToLowerInvariant();
        return lower.Contains("memory used");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _computer?.Close(); } catch { /* best effort */ }
        _computer    = null;
        _gpuHardware = null;
    }
}
