using System.Net;
using System.Security.Cryptography;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class DownloadServiceChecksumTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"aistudio_test_{Guid.NewGuid():N}");

    public DownloadServiceChecksumTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static DownloadService MakeService(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(status, body);
        var factory = new FakeHttpClientFactory(handler);
        return new DownloadService(factory);
    }

    private static string Sha256Of(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash  = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Správný checksum ──────────────────────────────────────────────────────

    [Fact]
    public async Task Download_CorrectChecksum_WritesFile()
    {
        const string content = "model weights content";
        var destPath = Path.Combine(_tempDir, "model.gguf");
        var svc      = MakeService(content);

        await svc.DownloadFileAsync(
            "https://example.com/model.gguf", destPath,
            expectedSha256: Sha256Of(content));

        File.Exists(destPath).Should().BeTrue();
        File.ReadAllText(destPath).Should().Be(content);
    }

    [Fact]
    public async Task Download_NoChecksum_WritesFileWithoutValidation()
    {
        const string content = "some model";
        var destPath = Path.Combine(_tempDir, "no_check.gguf");
        var svc      = MakeService(content);

        await svc.DownloadFileAsync("https://example.com/model.gguf", destPath);

        File.Exists(destPath).Should().BeTrue();
    }

    // ── Chybný checksum ───────────────────────────────────────────────────────

    [Fact]
    public async Task Download_WrongChecksum_ThrowsChecksumMismatchException()
    {
        const string content = "real content";
        var destPath = Path.Combine(_tempDir, "bad_hash.gguf");
        var svc      = MakeService(content);

        var act = () => svc.DownloadFileAsync(
            "https://example.com/model.gguf", destPath,
            expectedSha256: "0000000000000000000000000000000000000000000000000000000000000000");

        await act.Should().ThrowAsync<ChecksumMismatchException>()
            .Where(ex => ex.FilePath == destPath
                      && ex.Expected == "0000000000000000000000000000000000000000000000000000000000000000");
    }

    [Fact]
    public async Task Download_WrongChecksum_DeletesTmpFile()
    {
        const string content = "real content";
        var destPath = Path.Combine(_tempDir, "bad_hash2.gguf");
        var svc      = MakeService(content);

        try
        {
            await svc.DownloadFileAsync(
                "https://example.com/model.gguf", destPath,
                expectedSha256: "aabbcc");
        }
        catch (ChecksumMismatchException) { }

        // Finální soubor nesmí existovat
        File.Exists(destPath).Should().BeFalse();
        // Ani tmp soubor
        File.Exists(destPath + ".tmp").Should().BeFalse();
    }

    // ── ChecksumMismatchException vlastnosti ──────────────────────────────────

    [Fact]
    public async Task Download_WrongChecksum_ExceptionContainsActualHash()
    {
        const string content = "real content";
        var destPath = Path.Combine(_tempDir, "hash_props.gguf");
        var expected = "0000000000000000000000000000000000000000000000000000000000000000";
        var actual   = Sha256Of(content);
        var svc      = MakeService(content);

        var ex = await Assert.ThrowsAsync<ChecksumMismatchException>(() =>
            svc.DownloadFileAsync("https://example.com/m.gguf", destPath,
                                  expectedSha256: expected));

        ex.Expected.Should().Be(expected);
        ex.Actual.Should().Be(actual);
    }

    // ── Checksum case-insensitive ─────────────────────────────────────────────

    [Fact]
    public async Task Download_ChecksumUppercase_MatchesLowercase()
    {
        const string content = "model data";
        var destPath = Path.Combine(_tempDir, "case_test.gguf");
        var sha256Upper = Sha256Of(content).ToUpperInvariant();
        var svc = MakeService(content);

        // Uppercase checksum musí taky projít
        await svc.DownloadFileAsync("https://example.com/m.gguf", destPath,
                                    expectedSha256: sha256Upper);

        File.Exists(destPath).Should().BeTrue();
    }
}
