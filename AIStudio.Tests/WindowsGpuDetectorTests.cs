using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// WindowsGpuDetector samotnou detekci testovat nelze — vyžaduje WMI + reálnou
/// GPU + nvidia-smi binárku. Testujeme pure-logic helpers, které jsou
/// kandidáty na regresi pokud někdy přepneme PCI vendor IDs nebo přidáme
/// nový adapter blacklist.
/// </summary>
public class WindowsGpuDetectorTests
{
    // ── ExtractVendor — PCI vendor ID ────────────────────────────────────────

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2782&SUBSYS_xxx",  GpuVendor.Nvidia)]
    [InlineData(@"PCI\VEN_10de&DEV_2782",             GpuVendor.Nvidia)]   // lowercase
    [InlineData(@"PCI\VEN_1002&DEV_73EF&SUBSYS_xxx",  GpuVendor.Amd)]
    [InlineData(@"PCI\VEN_1022&DEV_15E7",             GpuVendor.Amd)]      // AMD APU
    [InlineData(@"PCI\VEN_8086&DEV_56A0",             GpuVendor.Intel)]
    public void ExtractVendor_ByPciId(string pnpId, GpuVendor expected)
    {
        // Název je úmyslně prázdný — testujeme pouze PCI ID branch
        WindowsGpuDetector.ExtractVendor(pnpId, string.Empty).Should().Be(expected);
    }

    // ── ExtractVendor — jméno fallback ───────────────────────────────────────

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070",      GpuVendor.Nvidia)]
    [InlineData("NVIDIA Quadro RTX A6000",      GpuVendor.Nvidia)]
    [InlineData("AMD Radeon RX 6750 XT",        GpuVendor.Amd)]
    [InlineData("Radeon Pro W7800",             GpuVendor.Amd)]
    [InlineData("Intel(R) UHD Graphics 770",    GpuVendor.Intel)]
    [InlineData("Intel Arc A770",               GpuVendor.Intel)]
    [InlineData("Intel Iris Xe Graphics",       GpuVendor.Intel)]
    public void ExtractVendor_ByNameKeyword(string name, GpuVendor expected)
    {
        // Bez PCI ID — testujeme pouze name-based fallback
        WindowsGpuDetector.ExtractVendor(string.Empty, name).Should().Be(expected);
    }

    [Fact]
    public void ExtractVendor_PciIdWinsOverName()
    {
        // Pokud PCI ID říká NVIDIA, ale název obsahuje "AMD", důvěřujeme PCI ID
        var vendor = WindowsGpuDetector.ExtractVendor(
            pnpId: @"PCI\VEN_10DE&DEV_2782",
            name:  "Mystery GPU labeled AMD");
        vendor.Should().Be(GpuVendor.Nvidia);
    }

    [Fact]
    public void ExtractVendor_UnknownReturnsUnknown()
    {
        WindowsGpuDetector.ExtractVendor("PCI\\VEN_FFFF&DEV_0000", "Mystery").Should().Be(GpuVendor.Unknown);
    }

    [Fact]
    public void ExtractVendor_PartialPnpId_FallsBackToName()
    {
        // PNP ID neobsahuje VEN_xxxx (např. virtuální adapter) → použij název
        WindowsGpuDetector.ExtractVendor("ROOT\\BasicDisplay\\0000", "AMD Radeon Pro")
            .Should().Be(GpuVendor.Amd);
    }

    // ── ChooseBackend ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(GpuVendor.Nvidia,  GpuBackend.Cuda)]
    [InlineData(GpuVendor.Amd,     GpuBackend.Vulkan)]
    [InlineData(GpuVendor.Intel,   GpuBackend.Vulkan)]
    [InlineData(GpuVendor.Apple,   GpuBackend.Cpu)]    // Detekce Metal je až v MacOsGpuDetector
    [InlineData(GpuVendor.Unknown, GpuBackend.Cpu)]
    public void ChooseBackend_MapsCorrectly(GpuVendor vendor, GpuBackend expected)
    {
        WindowsGpuDetector.ChooseBackend(vendor).Should().Be(expected);
    }

    // ── IsSoftwareAdapter ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Microsoft Basic Display Adapter",       true)]
    [InlineData("Microsoft Remote Display Adapter",      true)]
    [InlineData("Parsec Virtual Display Adapter",        true)]
    [InlineData("IDD HDMI Audio",                        true)]
    [InlineData("DisplayLink USB Adapter",               true)]
    [InlineData("NVIDIA GeForce RTX 4070",               false)]
    [InlineData("AMD Radeon RX 6750 XT",                 false)]
    [InlineData("Intel(R) UHD Graphics",                 false)]
    public void IsSoftwareAdapter_FiltersVirtual(string name, bool expected)
    {
        WindowsGpuDetector.IsSoftwareAdapter(name).Should().Be(expected);
    }

    // ── Gpu record convenience ────────────────────────────────────────────────

    [Fact]
    public void Gpu_VramGb_RoundsToOneDecimal()
    {
        var gpu = new Gpu(GpuVendor.Nvidia, "RTX 4070", 12_884_901_888L, GpuBackend.Cuda);
        gpu.VramGb.Should().BeApproximately(12.0, 0.05);
    }

    [Fact]
    public void Gpu_HasGpuAcceleration_TrueForGpuBackends()
    {
        new Gpu(GpuVendor.Nvidia, "x", 0, GpuBackend.Cuda).HasGpuAcceleration.Should().BeTrue();
        new Gpu(GpuVendor.Amd,    "x", 0, GpuBackend.Vulkan).HasGpuAcceleration.Should().BeTrue();
        new Gpu(GpuVendor.Apple,  "x", 0, GpuBackend.Metal).HasGpuAcceleration.Should().BeTrue();
        new Gpu(GpuVendor.Unknown, "x", 0, GpuBackend.Cpu).HasGpuAcceleration.Should().BeFalse();
    }
}
