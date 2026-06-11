using AIStudio.Core.Models;
using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class VideoLoraDatasetBuilderTests
{
    private static CandidateFrame Frame(string path, string caption = "") =>
        new(path, SourceVideoPath: "v.mp4", Timestamp: TimeSpan.FromSeconds(1), Caption: caption);

    [Fact]
    public void Build_TokenOnly_CaptionIsJustTrigger()
    {
        var ds = VideoLoraDatasetBuilder.Build(
            new[] { Frame("a.png", "a blonde woman on a beach") }, "Nikol", tokenOnly: true);

        ds.Should().ContainSingle();
        ds[0].ImagePath.Should().Be("a.png");
        ds[0].Caption.Should().Be("nikol");
    }

    [Fact]
    public void Build_Descriptive_PrependsTrigger()
    {
        var ds = VideoLoraDatasetBuilder.Build(
            new[] { Frame("a.png", "a blonde woman on a beach") }, "Nikol", tokenOnly: false);

        ds[0].Caption.Should().Be("nikol, a blonde woman on a beach");
    }

    [Fact]
    public void Build_Descriptive_EmptyCaption_FallsBackToTrigger()
    {
        var ds = VideoLoraDatasetBuilder.Build(
            new[] { Frame("a.png", "") }, "Rex", tokenOnly: false);

        ds[0].Caption.Should().Be("rex");
    }

    [Fact]
    public void NormalizeTrigger_SanitizesToSafeToken()
    {
        VideoLoraDatasetBuilder.NormalizeTrigger("Nikol Smith #1").Should().Be("nikol_smith__1");
        VideoLoraDatasetBuilder.NormalizeTrigger("my-dog_2").Should().Be("my-dog_2");
        VideoLoraDatasetBuilder.NormalizeTrigger("   ").Should().BeEmpty();
    }

    [Fact]
    public void Build_PreservesOrderAndCount()
    {
        var ds = VideoLoraDatasetBuilder.Build(
            new[] { Frame("a.png", "x"), Frame("b.png", "y"), Frame("c.png", "z") },
            "t", tokenOnly: false);

        ds.Select(d => d.ImagePath).Should().Equal("a.png", "b.png", "c.png");
    }
}
