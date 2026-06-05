using AIStudio.Core.Interfaces;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

public sealed class ComfyUpdateServiceTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("aistudio_comfyupd_").FullName;

    private ComfyUpdateService Make() =>
        new(Substitute.For<IComfyService>(), Substitute.For<ISettingsService>());

    [Fact]
    public void IsGitRepo_NoDotGit_False()
    {
        Make().IsGitRepo(_tmp).Should().BeFalse();
    }

    [Fact]
    public void IsGitRepo_WithDotGit_True()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, ".git"));
        Make().IsGitRepo(_tmp).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsGitRepo_BlankOrMissing_False(string? dir)
    {
        Make().IsGitRepo(dir).Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* ignore */ }
    }
}
