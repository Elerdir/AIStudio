using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class NativeSamplerMapTests
{
    [Theory]
    [InlineData("euler", "euler")]
    [InlineData("euler_ancestral", "euler_a")]
    [InlineData("euler_a", "euler_a")]
    [InlineData("dpmpp_2m", "dpm++2m")]
    [InlineData("dpmpp_2s_ancestral", "dpm++2s_a")]
    [InlineData("dpmpp_2m_sde", "dpm++2mv2")]
    [InlineData("heun", "heun")]
    [InlineData("dpm_2", "dpm2")]
    [InlineData("lcm", "lcm")]
    [InlineData("ddim", "ddim_trailing")]
    public void ToSdCpp_MapsKnownSamplers(string input, string expected)
    {
        NativeSamplerMap.ToSdCpp(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("DPMPP_2M")]      // case-insensitive
    [InlineData("  euler  ")]     // ořez mezer
    public void ToSdCpp_NormalizesInput(string input)
    {
        NativeSamplerMap.ToSdCpp(input).Should().NotBe(string.Empty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("uni_pc")]        // bez ekvivalentu → default
    [InlineData("nonsense")]
    public void ToSdCpp_UnknownOrEmpty_FallsBackToDefault(string? input)
    {
        NativeSamplerMap.ToSdCpp(input).Should().Be(NativeSamplerMap.Default);
    }

    [Fact]
    public void HasDirectEquivalent_TrueForKnown_FalseForFallback()
    {
        NativeSamplerMap.HasDirectEquivalent("dpmpp_2m").Should().BeTrue();
        NativeSamplerMap.HasDirectEquivalent("euler").Should().BeTrue();
        NativeSamplerMap.HasDirectEquivalent("uni_pc").Should().BeFalse();
        NativeSamplerMap.HasDirectEquivalent(null).Should().BeFalse();
    }
}
