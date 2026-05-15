using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// TokenEstimator je pure static. Testujeme krajní hodnoty, sum behavior
/// a clamp v UsagePercent. Změna heuristiky (CharsPerToken) tady musí
/// projít — pokud někdy přepneme na reálný tokenizer, testy aktualizujeme.
/// </summary>
public class TokenEstimatorTests
{
    // ── EstimateText ──────────────────────────────────────────────────────────

    [Fact]
    public void EstimateText_Null_ReturnsZero()
    {
        TokenEstimator.EstimateText(null).Should().Be(0);
    }

    [Fact]
    public void EstimateText_Empty_ReturnsZero()
    {
        TokenEstimator.EstimateText(string.Empty).Should().Be(0);
    }

    [Theory]
    [InlineData("",       0)]
    [InlineData("a",      0)]   // 1 znak → /4 = 0
    [InlineData("abcd",   1)]   // 4 znaky → 1 token
    [InlineData("abcdef", 1)]   // 6 znaků → /4 = 1
    [InlineData("abcdefgh", 2)] // 8 znaků → 2 tokeny
    public void EstimateText_DividedByFour(string text, int expected)
    {
        TokenEstimator.EstimateText(text).Should().Be(expected);
    }

    [Fact]
    public void EstimateText_CzechWithDiacritics_CountsByCharsNotBytes()
    {
        // 8 znaků (kód + diakritika), 1 char per code point — ne per UTF-8 byte
        var text = "ěščřžýáí";
        text.Length.Should().Be(8);
        TokenEstimator.EstimateText(text).Should().Be(2);
    }

    // ── EstimateMessages ──────────────────────────────────────────────────────

    [Fact]
    public void EstimateMessages_Null_ReturnsZero()
    {
        TokenEstimator.EstimateMessages(null).Should().Be(0);
    }

    [Fact]
    public void EstimateMessages_Empty_ReturnsZero()
    {
        TokenEstimator.EstimateMessages(Array.Empty<string>()).Should().Be(0);
    }

    [Fact]
    public void EstimateMessages_SumsAllEntries()
    {
        var messages = new[] { "abcd", "abcdefgh", "abcdefghijkl" }; // 1 + 2 + 3
        TokenEstimator.EstimateMessages(messages).Should().Be(6);
    }

    [Fact]
    public void EstimateMessages_NullEntries_TreatedAsZero()
    {
        var messages = new[] { "abcd", null, "abcdefgh", string.Empty };
        TokenEstimator.EstimateMessages(messages).Should().Be(3); // 1 + 0 + 2 + 0
    }

    // ── UsagePercent ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0)]      // edge: žádný model
    [InlineData(100, 0, 0)]    // edge: dělení nulou
    [InlineData(0, 8000, 0)]   // prázdná konverzace
    [InlineData(4000, 8000, 50)]
    [InlineData(8000, 8000, 100)]
    [InlineData(16000, 8000, 100)] // clamp nad 100
    public void UsagePercent_Clamped(int tokens, int max, double expected)
    {
        TokenEstimator.UsagePercent(tokens, max).Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void UsagePercent_NegativeTokens_ReturnsZero()
    {
        // Defenzivní — pokud někdy bude EstimatedTokens podivné, UI nepadne
        TokenEstimator.UsagePercent(-100, 8000).Should().Be(0);
    }
}
