using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class VideoFrameExtractorTests
{
    [Fact]
    public void ParseDuration_ReadsFfmpegStderrLine()
    {
        const string stderr =
            "Input #0, mov,mp4, from 'clip.mp4':\n" +
            "  Duration: 00:01:23.45, start: 0.000000, bitrate: 1234 kb/s\n" +
            "  Stream #0:0...";

        var d = VideoFrameExtractor.ParseDuration(stderr);
        d.Should().NotBeNull();
        d!.Value.TotalSeconds.Should().BeApproximately(83.45, 0.001);
    }

    [Fact]
    public void ParseDuration_HoursMinutesSeconds()
    {
        VideoFrameExtractor.ParseDuration("  Duration: 02:03:04.00, start: 0")!
            .Value.TotalSeconds.Should().BeApproximately(2 * 3600 + 3 * 60 + 4, 0.001);
    }

    [Fact]
    public void ParseDuration_NoDurationLine_ReturnsNull()
    {
        VideoFrameExtractor.ParseDuration("nic tu není").Should().BeNull();
        VideoFrameExtractor.ParseDuration("").Should().BeNull();
        VideoFrameExtractor.ParseDuration(null).Should().BeNull();
    }

    [Fact]
    public void BuildExtractArgs_PutsSeekBeforeInput_AndSingleFrame()
    {
        var args = VideoFrameExtractor.BuildExtractArgs("in.mp4", TimeSpan.FromSeconds(12.5), "out.jpg");

        // -ss musí být před -i (rychlé seekování)
        var ssIdx = args.ToList().IndexOf("-ss");
        var iIdx  = args.ToList().IndexOf("-i");
        ssIdx.Should().BeGreaterThanOrEqualTo(0);
        ssIdx.Should().BeLessThan(iIdx);

        args.Should().ContainInOrder("-frames:v", "1");
        args[ssIdx + 1].Should().Be("12.5");           // invariant decimal point
        args.Last().Should().Be("out.jpg");
    }

    [Fact]
    public void BuildExtractArgs_FormatsTimestampInvariant()
    {
        // I na lokále s desetinnou čárkou musí být tečka, jinak ffmpeg seek selže.
        var args = VideoFrameExtractor.BuildExtractArgs("in.mp4", TimeSpan.FromSeconds(7.125), "o.jpg");
        args[args.ToList().IndexOf("-ss") + 1].Should().Be("7.125");
    }
}
