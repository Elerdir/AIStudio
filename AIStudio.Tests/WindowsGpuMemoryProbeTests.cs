using LibreHardwareMonitor.Hardware;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// WindowsGpuMemoryProbe samotnou LHM detekci testovat nelze — vyžaduje
/// reálnou GPU + admin/runtime privileges + driver instalovaný. Testujeme
/// pure-logic helpers (sensor name matching, vendor type detection).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsGpuMemoryProbeTests
{
    // ── IsGpuHardware — vendor type přiřazení ────────────────────────────────

    [Theory]
    [InlineData(HardwareType.GpuNvidia,  true)]
    [InlineData(HardwareType.GpuAmd,     true)]
    [InlineData(HardwareType.GpuIntel,   true)]
    [InlineData(HardwareType.Cpu,        false)]
    [InlineData(HardwareType.Memory,     false)]
    [InlineData(HardwareType.Storage,    false)]
    [InlineData(HardwareType.Motherboard, false)]
    public void IsGpuHardware_IdentifiesAllGpuTypes(HardwareType type, bool expected)
    {
        WindowsGpuMemoryProbe.IsGpuHardware(type).Should().Be(expected);
    }

    // ── IsTotalVramSensor — name matching ───────────────────────────────────

    [Theory]
    [InlineData("GPU Memory Total",                true)]
    [InlineData("D3D Dedicated Memory Total",      true)]
    [InlineData("D3D Shared Memory Total",         true)]
    [InlineData("gpu memory total",                true)]   // case-insensitive
    [InlineData("GPU MEMORY TOTAL",                true)]
    [InlineData("GPU Memory Used",                 false)]
    [InlineData("GPU Memory Free",                 false)]
    [InlineData("GPU Memory",                      false)]  // bez "Total" suffix
    [InlineData("",                                false)]
    [InlineData("GPU Core",                        false)]
    public void IsTotalVramSensor_MatchesKnownNames(string sensorName, bool expected)
    {
        WindowsGpuMemoryProbe.IsTotalVramSensor(sensorName).Should().Be(expected);
    }
}
