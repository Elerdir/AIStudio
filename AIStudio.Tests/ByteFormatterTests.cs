using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class ByteFormatterTests
{
    [Theory]
    [InlineData(0,     "0 B")]
    [InlineData(1,     "1 B")]
    [InlineData(1023,  "1023 B")]   // < 1 KB → bytes
    [InlineData(1024,  "1 KB")]
    [InlineData(2048,  "2 KB")]
    [InlineData(1_048_575, "1024 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(1_572_864, "1.5 MB")]
    [InlineData(1_073_741_823, "1024.0 MB")]
    [InlineData(1_073_741_824, "1.00 GB")]
    [InlineData(7_300_000_000L, "6.80 GB")]    // ~ FLUX Schnell size
    public void Format_ProducesExpectedString(long bytes, string expected)
    {
        ByteFormatter.Format(bytes).Should().Be(expected);
    }
}
