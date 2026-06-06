using AIStudio.Core.Models;
using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class NativeModelDefaultsTests
{
    [Theory]
    [InlineData(NativeModelFamily.Sd1, 512, 512)]
    [InlineData(NativeModelFamily.Sd2, 768, 768)]
    [InlineData(NativeModelFamily.Sdxl, 1024, 1024)]
    [InlineData(NativeModelFamily.Flux, 1024, 1024)]
    public void For_ReturnsExpectedResolution(NativeModelFamily family, int w, int h)
    {
        var (width, height, _, _) = NativeModelDefaults.For(family);
        width.Should().Be(w);
        height.Should().Be(h);
    }

    [Fact]
    public void For_Flux_UsesLowCfg()
    {
        // FLUX je guidance-distilled → CFG ~1 (vyšší rozbíjí výstup)
        NativeModelDefaults.For(NativeModelFamily.Flux).Cfg.Should().BeLessThan(2.0);
    }

    [Fact]
    public void For_Unknown_FallsBackToSafeDefaults()
    {
        var d = NativeModelDefaults.For(NativeModelFamily.Unknown);
        d.Width.Should().Be(512);
        d.Steps.Should().BeGreaterThan(0);
        d.Cfg.Should().BeGreaterThan(0);
    }
}
