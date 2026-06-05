using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class VideoSegmentPlannerTests
{
    [Theory]
    [InlineData(3, 5)]     // pod minimem → min 5
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(7, 9)]     // 7 → nejbližší 4n+1 = 9
    [InlineData(33, 33)]
    [InlineData(80, 81)]
    [InlineData(201, 201)]  // jen snap na 4n+1 (zastropování řeší Plan, ne tahle metoda)
    public void RoundToWanLength_SnapsTo4nPlus1(int input, int expected)
    {
        VideoSegmentPlanner.RoundToWanLength(input).Should().Be(expected);
    }

    [Fact]
    public void Plan_ShortClip_SingleSegment()
    {
        var plan = VideoSegmentPlanner.Plan(targetSeconds: 5, fps: 16);
        plan.Should().HaveCount(1);
        plan[0].Should().BeLessOrEqualTo(VideoSegmentPlanner.MaxFramesPerSegment);
    }

    [Fact]
    public void Plan_AllSegments_AreValidWanLengths()
    {
        var plan = VideoSegmentPlanner.Plan(targetSeconds: 30, fps: 16);
        plan.Should().NotBeEmpty();
        foreach (var len in plan)
        {
            (len % 4).Should().Be(1, "Wan vyžaduje délku 4n+1");
            len.Should().BeInRange(VideoSegmentPlanner.MinFramesPerSegment, VideoSegmentPlanner.MaxFramesPerSegment);
        }
    }

    [Fact]
    public void Plan_LongerTarget_ProducesMoreSegments()
    {
        var short10 = VideoSegmentPlanner.Plan(10, 16);
        var long60  = VideoSegmentPlanner.Plan(60, 16);
        long60.Count.Should().BeGreaterThan(short10.Count);
    }

    [Fact]
    public void Plan_EffectiveDuration_CloseToTarget()
    {
        const int target = 30, fps = 16;
        var plan = VideoSegmentPlanner.Plan(target, fps);
        var eff  = VideoSegmentPlanner.EffectiveSeconds(plan, fps);
        // Díky překryvu a zaokrouhlení na 4n+1 se trefíme zhruba (tolerance ~1 segment).
        eff.Should().BeGreaterOrEqualTo(target - 0.5);
        eff.Should().BeLessThan(target + 6);
    }

    [Fact]
    public void Plan_MinimizesSegmentCount_SegmentsNearMax()
    {
        // 30 s @16fps = 480 snímků; s překryvem stačí 6 segmentů po ~81.
        var plan = VideoSegmentPlanner.Plan(30, 16);
        plan.Count.Should().Be(6);
        plan.Should().OnlyContain(len => len >= 77);   // blízko stropu (minimalizace driftu)
    }

    [Fact]
    public void EffectiveSeconds_AccountsForOverlap()
    {
        // 2 segmenty po 81 snímcích, 16 fps: 81 + (81-1) = 161 snímků = ~10.06 s
        var eff = VideoSegmentPlanner.EffectiveSeconds(new[] { 81, 81 }, 16);
        eff.Should().BeApproximately(161 / 16.0, 0.01);
    }
}
