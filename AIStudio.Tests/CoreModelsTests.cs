using AIStudio.Core.Models;
using FluentAssertions;

namespace AIStudio.Tests;

public class DownloadProgressInfoTests
{
    [Fact]
    public void Percent_WhenTotalKnown_CalculatesCorrectly()
    {
        var info = new DownloadProgressInfo(Downloaded: 50, Total: 100, BytesPerSecond: 1024);
        info.Percent.Should().BeApproximately(50.0, precision: 0.001);
    }

    [Fact]
    public void Percent_WhenTotalZero_ReturnsZero()
    {
        var info = new DownloadProgressInfo(Downloaded: 10, Total: 0, BytesPerSecond: 0);
        info.Percent.Should().Be(0);
    }

    [Fact]
    public void Percent_WhenComplete_Returns100()
    {
        var info = new DownloadProgressInfo(Downloaded: 1024, Total: 1024, BytesPerSecond: 5_000_000);
        info.Percent.Should().BeApproximately(100.0, precision: 0.001);
    }

    [Fact]
    public void Record_Equality_Works()
    {
        var a = new DownloadProgressInfo(100, 200, 5000);
        var b = new DownloadProgressInfo(100, 200, 5000);
        a.Should().Be(b);
    }
}

public class ConversationRecordTests
{
    [Fact]
    public void WithExpression_CreatesModifiedCopy()
    {
        var original = new ConversationRecord(
            Id: "abc",
            Title: "Původní",
            ModelName: "Phi-4",
            MaxTokens: 2048,
            SystemPrompt: "",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        var renamed = original with { Title = "Nový název" };

        renamed.Id.Should().Be("abc");
        renamed.Title.Should().Be("Nový název");
        renamed.ModelName.Should().Be("Phi-4");
        original.Title.Should().Be("Původní"); // immutability
    }

    [Fact]
    public void IsPinned_DefaultsFalse()
    {
        var conv = new ConversationRecord("id", "t", "m", 2048, "", DateTime.UtcNow, DateTime.UtcNow);
        conv.IsPinned.Should().BeFalse();
    }
}
