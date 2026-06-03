using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class ComfyVersionTests
{
    [Fact]
    public void Parse_DoubleQuoted_ExtractsVersion() =>
        ComfyVersion.Parse("__version__ = \"0.3.40\"").Should().Be("0.3.40");

    [Fact]
    public void Parse_SingleQuoted_ExtractsVersion() =>
        ComfyVersion.Parse("# comment\n__version__ = '0.3.41'\n").Should().Be("0.3.41");

    [Fact]
    public void Parse_NoVersion_ReturnsNull() =>
        ComfyVersion.Parse("print('hello')").Should().BeNull();

    [Fact]
    public void Parse_NullOrEmpty_ReturnsNull()
    {
        ComfyVersion.Parse(null).Should().BeNull();
        ComfyVersion.Parse("").Should().BeNull();
    }

    [Fact]
    public void ReadFromDirectory_MissingDir_ReturnsNull() =>
        ComfyVersion.ReadFromDirectory(@"C:\does\not\exist\xyz").Should().BeNull();

    [Fact]
    public void ReadFromDirectory_ReadsFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "comfyver_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "comfyui_version.py"), "__version__ = \"0.3.99\"\n");
            ComfyVersion.ReadFromDirectory(dir).Should().Be("0.3.99");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
