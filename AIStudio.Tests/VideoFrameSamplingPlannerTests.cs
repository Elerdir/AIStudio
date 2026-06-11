using AIStudio.Core.Models;
using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class VideoFrameSamplingPlannerTests
{
    private static FrameSamplingOptions Opts(int n = 20, double s = 0.05, double e = 0.05) =>
        new(FramesPerVideo: n, SkipStartFraction: s, SkipEndFraction: e);

    [Fact]
    public void Plan_ReturnsRequestedCount()
    {
        VideoFrameSamplingPlanner.Plan(60, Opts(20)).Should().HaveCount(20);
    }

    [Fact]
    public void Plan_StaysWithinSkipWindow()
    {
        // 60 s, skip 5 % na obou koncích → okno [3 s, 57 s], midpoint vzorkování uvnitř.
        var stamps = VideoFrameSamplingPlanner.Plan(60, Opts(20));

        stamps.First().TotalSeconds.Should().BeGreaterThanOrEqualTo(3.0);
        stamps.Last().TotalSeconds.Should().BeLessThanOrEqualTo(57.0);
    }

    [Fact]
    public void Plan_TimestampsStrictlyIncreasing()
    {
        var stamps = VideoFrameSamplingPlanner.Plan(120, Opts(15));
        for (var i = 1; i < stamps.Count; i++)
            stamps[i].Should().BeGreaterThan(stamps[i - 1]);
    }

    [Fact]
    public void Plan_SingleFrame_LandsInMiddleOfWindow()
    {
        // 100 s, skip 10/10 → okno [10, 90], midpoint = 50 s.
        var stamps = VideoFrameSamplingPlanner.Plan(100, Opts(1, 0.10, 0.10));
        stamps.Should().ContainSingle();
        stamps[0].TotalSeconds.Should().BeApproximately(50.0, 0.001);
    }

    [Fact]
    public void Plan_ZeroOrNegativeDuration_ReturnsEmpty()
    {
        VideoFrameSamplingPlanner.Plan(0, Opts()).Should().BeEmpty();
        VideoFrameSamplingPlanner.Plan(-5, Opts()).Should().BeEmpty();
    }

    [Fact]
    public void Plan_ClampsCountToMax()
    {
        VideoFrameSamplingPlanner.Plan(600, Opts(10_000))
            .Should().HaveCount(VideoFrameSamplingPlanner.MaxFramesPerVideo);
    }

    [Fact]
    public void Plan_DegenerateSkip_FallsBackToWholeVideo()
    {
        // Skip frakce se clampují na 0.49 → součet 0.98, okno by bylo titěrné, ale
        // pořád kladné; ověříme, že to nespadne a značky jsou uvnitř [0, duration].
        var stamps = VideoFrameSamplingPlanner.Plan(50, Opts(5, 0.9, 0.9));
        stamps.Should().HaveCount(5);
        stamps.First().TotalSeconds.Should().BeGreaterThanOrEqualTo(0);
        stamps.Last().TotalSeconds.Should().BeLessThanOrEqualTo(50);
    }
}
