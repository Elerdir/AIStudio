using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public sealed class ModelStorageCleanerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aistudio_cleaner_").FullName;

    private string Touch(string relative, int bytes = 0)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void ScanOrphanTmp_FindsTmp_Recursively_SumsBytes()
    {
        Touch("a.tmp", 100);
        Touch("loras/b.tmp", 50);
        Touch("model.gguf", 999);   // ne .tmp → ignorováno

        var (count, bytes) = ModelStorageCleaner.ScanOrphanTmp(_dir, new HashSet<string>());

        count.Should().Be(2);
        bytes.Should().Be(150);
    }

    [Fact]
    public void ScanOrphanTmp_SkipsProtected()
    {
        var keep = Touch("active.tmp", 10);
        Touch("orphan.tmp", 20);

        var prot = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keep };
        var (count, bytes) = ModelStorageCleaner.ScanOrphanTmp(_dir, prot);

        count.Should().Be(1);
        bytes.Should().Be(20);
    }

    [Fact]
    public void DeleteOrphanTmp_DeletesUnprotected_KeepsProtected()
    {
        var keep   = Touch("paused.tmp", 10);
        var orphan = Touch("sub/old.tmp", 5);

        var prot = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keep };
        var deleted = ModelStorageCleaner.DeleteOrphanTmp(_dir, prot);

        deleted.Should().Be(1);
        File.Exists(orphan).Should().BeFalse();
        File.Exists(keep).Should().BeTrue();
    }

    [Fact]
    public void Scan_MissingDir_ReturnsZero()
    {
        var (count, bytes) = ModelStorageCleaner.ScanOrphanTmp(Path.Combine(_dir, "nope"), new HashSet<string>());
        count.Should().Be(0);
        bytes.Should().Be(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
