using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// MacOsGpuDetector samotnou detekci testovat nelze — vyžaduje macOS
/// runtime + system_profiler binárku. Testujeme pure-logic JSON parser
/// + pomocné metody (vendor detekce, velikost stringu).
///
/// Třída nese [SupportedOSPlatform("macos")] kvůli CA1416 — testované
/// metody jsou sice čistě string/JSON manipulace, ale dědí atribut z
/// MacOsGpuDetector. Atribut je pouze analyzer hint, nemá runtime efekt:
/// testy se spustí i na Windows CI runneru a projdou (stejný pattern
/// jako WindowsGpuDetectorTests).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class MacOsGpuDetectorTests
{
    // ── IsAppleVendor ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Apple (0x106b)",  true)]
    [InlineData("apple (0x106b)",  true)]   // case-insensitive
    [InlineData("APPLE INC.",      true)]
    [InlineData("Apple",           true)]   // novější system_profiler bez hex
    [InlineData("AMD (0x1002)",    false)]
    [InlineData("Intel Inc.",      false)]
    [InlineData("",                false)]
    public void IsAppleVendor_DetectsAppleString(string vendor, bool expected)
    {
        MacOsGpuDetector.IsAppleVendor(vendor).Should().Be(expected);
    }

    // ── ParseSizeString ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("16 GB",   17_179_869_184L)]
    [InlineData("8 GB",     8_589_934_592L)]
    [InlineData("1024 MB",  1_073_741_824L)]
    [InlineData("2 GiB",    2_147_483_648L)]
    [InlineData("",                      0L)]
    [InlineData("nonsense",              0L)]
    [InlineData("12",                    0L)]   // chybí jednotka
    public void ParseSizeString_ConvertsToBytes(string input, long expected)
    {
        MacOsGpuDetector.ParseSizeString(input).Should().Be(expected);
    }

    // ── ParseAppleSilicon — happy path ───────────────────────────────────────

    [Fact]
    public void ParseAppleSilicon_M2Pro_ReturnsMetalBackend()
    {
        // Reálná struktura odpovědi system_profiler -json SPDisplaysDataType
        // pro M2 Pro 16 GB. Klíčová pole: _name, spdisplays_vendor, spdisplays_vram_shared.
        var json = """
        {
            "SPDisplaysDataType": [
                {
                    "_name": "Apple M2 Pro",
                    "spdisplays_vendor": "Apple (0x106b)",
                    "spdisplays_vram_shared": "16 GB",
                    "spdisplays_metalfamily": "spdisplays_metal3",
                    "spdisplays_device-id": "0x0000"
                }
            ]
        }
        """;

        var gpu = MacOsGpuDetector.ParseAppleSilicon(json);

        gpu.Should().NotBeNull();
        gpu!.Vendor.Should().Be(GpuVendor.Apple);
        gpu.Name.Should().Be("Apple M2 Pro");
        gpu.Backend.Should().Be(GpuBackend.Metal);
        gpu.VramBytes.Should().Be(17_179_869_184L);
        gpu.VramGb.Should().BeApproximately(16.0, 0.05);
    }

    [Fact]
    public void ParseAppleSilicon_M4Max_NamePreserved()
    {
        // Apple SoC s vyšší unified memory — 48 GB / 64 GB / 128 GB jsou reálné
        // konfigurace pro M3 Max / M4 Max. Parser musí zvládnout libovolný název.
        var json = """
        {
            "SPDisplaysDataType": [
                {
                    "_name": "Apple M4 Max",
                    "spdisplays_vendor": "Apple",
                    "spdisplays_vram_shared": "64 GB"
                }
            ]
        }
        """;

        var gpu = MacOsGpuDetector.ParseAppleSilicon(json);
        gpu!.Name.Should().Be("Apple M4 Max");
        gpu.VramGb.Should().BeApproximately(64.0, 0.05);
    }

    // ── ParseAppleSilicon — non-Apple ────────────────────────────────────────

    [Fact]
    public void ParseAppleSilicon_AmdRadeon_ReturnsNull()
    {
        // Intel Mac 2019 s diskrétní AMD GPU — nechceme ho podporovat
        // (uživatel řekl: jenom Apple Silicon Mx)
        var json = """
        {
            "SPDisplaysDataType": [
                {
                    "_name": "AMD Radeon Pro 5500M",
                    "spdisplays_vendor": "AMD (0x1002)",
                    "spdisplays_vram": "4 GB"
                }
            ]
        }
        """;

        MacOsGpuDetector.ParseAppleSilicon(json).Should().BeNull();
    }

    [Fact]
    public void ParseAppleSilicon_OnlyIntelIris_ReturnsNull()
    {
        // Starší Mac s Intel iGPU bez diskrétní karty — taky nepodporujeme
        var json = """
        {
            "SPDisplaysDataType": [
                {
                    "_name": "Intel UHD Graphics 630",
                    "spdisplays_vendor": "Intel Inc.",
                    "spdisplays_vram_shared": "1536 MB"
                }
            ]
        }
        """;

        MacOsGpuDetector.ParseAppleSilicon(json).Should().BeNull();
    }

    // ── ParseAppleSilicon — multiple adapters ───────────────────────────────

    [Fact]
    public void ParseAppleSilicon_MultipleAdapters_PicksFirstApple()
    {
        // Hypoteticky externí eGPU + Apple SoC — vzít Apple, externí ignorovat
        var json = """
        {
            "SPDisplaysDataType": [
                { "_name": "AMD Radeon RX 6800 (eGPU)", "spdisplays_vendor": "AMD (0x1002)" },
                { "_name": "Apple M3",                  "spdisplays_vendor": "Apple (0x106b)",
                  "spdisplays_vram_shared": "8 GB" }
            ]
        }
        """;

        var gpu = MacOsGpuDetector.ParseAppleSilicon(json);
        gpu.Should().NotBeNull();
        gpu!.Vendor.Should().Be(GpuVendor.Apple);
        gpu.Name.Should().Be("Apple M3");
    }

    // ── ParseAppleSilicon — chybějící VRAM ──────────────────────────────────

    [Fact]
    public void ParseAppleSilicon_NoVramField_VramBytesZero()
    {
        // Některé verze system_profiler nedodají vram_shared (dynamic alloc)
        var json = """
        {
            "SPDisplaysDataType": [
                {
                    "_name": "Apple M1",
                    "spdisplays_vendor": "Apple (0x106b)"
                }
            ]
        }
        """;

        var gpu = MacOsGpuDetector.ParseAppleSilicon(json);
        gpu.Should().NotBeNull();
        gpu!.VramBytes.Should().Be(0);
        gpu.Backend.Should().Be(GpuBackend.Metal); // Backend funguje i bez VRAM info
    }

    // ── ParseAppleSilicon — malformed JSON ──────────────────────────────────

    [Fact]
    public void ParseAppleSilicon_NoDataType_ReturnsNull()
    {
        var json = """{ "SomethingElse": [] }""";
        MacOsGpuDetector.ParseAppleSilicon(json).Should().BeNull();
    }

    [Fact]
    public void ParseAppleSilicon_EmptyArray_ReturnsNull()
    {
        var json = """{ "SPDisplaysDataType": [] }""";
        MacOsGpuDetector.ParseAppleSilicon(json).Should().BeNull();
    }
}
