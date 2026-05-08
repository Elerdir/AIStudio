using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace AIStudio.Tests;

public class SqliteImageRepositoryTests : IAsyncLifetime
{
    private readonly string _dbName = $"imgtest_{Guid.NewGuid():N}";
    private string ConnStr => $"Data Source=file:{_dbName}?mode=memory&cache=shared";

    private SqliteImageRepository _repo   = null!;
    private SqliteConnection      _anchor = null!;

    public async Task InitializeAsync()
    {
        _anchor = new SqliteConnection(ConnStr);
        await _anchor.OpenAsync();

        _repo = new SqliteImageRepository(ConnStr);
        await _repo.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _anchor.CloseAsync();
        _anchor.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ImageRecord MakeImage(string? id = null) => new(
        Id:          id ?? Guid.NewGuid().ToString(),
        FilePath:    @"C:\images\test.png",
        Prompt:      "kočka na střeše",
        ModelName:   "stable-diffusion-xl",
        Seed:        42L,
        Width:       512,
        Height:      512,
        Steps:       20,
        Cfg:         7.0,
        GeneratedAt: DateTime.UtcNow);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAllImages_EmptyDb_ReturnsEmpty()
    {
        var result = await _repo.LoadAllImagesAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveImage_ThenLoad_RoundTrips()
    {
        var img = MakeImage();
        await _repo.SaveImageAsync(img);

        var all = await _repo.LoadAllImagesAsync();

        all.Should().HaveCount(1);
        var loaded = all[0];
        loaded.Id.Should().Be(img.Id);
        loaded.Prompt.Should().Be(img.Prompt);
        loaded.Width.Should().Be(512);
        loaded.Height.Should().Be(512);
        loaded.Seed.Should().Be(42L);
        loaded.Cfg.Should().BeApproximately(7.0, 0.001);
    }

    [Fact]
    public async Task SaveImage_Upsert_UpdatesExisting()
    {
        var img = MakeImage();
        await _repo.SaveImageAsync(img);

        var updated = img with { Prompt = "pes v parku", Steps = 30 };
        await _repo.SaveImageAsync(updated);

        var all = await _repo.LoadAllImagesAsync();
        all.Should().HaveCount(1);
        all[0].Prompt.Should().Be("pes v parku");
        all[0].Steps.Should().Be(30);
    }

    [Fact]
    public async Task DeleteImage_RemovesRecord()
    {
        var img = MakeImage();
        await _repo.SaveImageAsync(img);

        await _repo.DeleteImageAsync(img.Id);

        var all = await _repo.LoadAllImagesAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleImages_LoadedNewestFirst()
    {
        var older = MakeImage() with { GeneratedAt = DateTime.UtcNow.AddHours(-2) };
        var newer = MakeImage() with { GeneratedAt = DateTime.UtcNow };
        await _repo.SaveImageAsync(older);
        await _repo.SaveImageAsync(newer);

        var all = await _repo.LoadAllImagesAsync();
        all.Should().HaveCount(2);
        all[0].Id.Should().Be(newer.Id);
        all[1].Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task DeleteImage_NonExistentId_DoesNotThrow()
    {
        await _repo.Invoking(r => r.DeleteImageAsync("neexistující-id"))
                   .Should().NotThrowAsync();
    }
}
