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
        Sampler:     "euler",
        Scheduler:   "simple",
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

    // ── Paginace ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountImagesAsync_EmptyDb_ReturnsZero()
    {
        var count = await _repo.CountImagesAsync();
        count.Should().Be(0);
    }

    [Fact]
    public async Task CountImagesAsync_AfterInserts_ReturnsTotal()
    {
        for (var i = 0; i < 5; i++)
            await _repo.SaveImageAsync(MakeImage());

        var count = await _repo.CountImagesAsync();
        count.Should().Be(5);
    }

    [Fact]
    public async Task LoadImagesPagedAsync_FirstPage_ReturnsNewestN()
    {
        // 10 obrázků se stoupajícím GeneratedAt — nejnovější má vyšší index
        var ids = new List<string>();
        var baseTime = DateTime.UtcNow.AddHours(-10);
        for (var i = 0; i < 10; i++)
        {
            var img = MakeImage() with { GeneratedAt = baseTime.AddMinutes(i) };
            ids.Add(img.Id);
            await _repo.SaveImageAsync(img);
        }

        // První stránka po 3 = nejnovější 3 (i=9, 8, 7)
        var page = await _repo.LoadImagesPagedAsync(skip: 0, take: 3);

        page.Should().HaveCount(3);
        page[0].Id.Should().Be(ids[9]);
        page[1].Id.Should().Be(ids[8]);
        page[2].Id.Should().Be(ids[7]);
    }

    [Fact]
    public async Task LoadImagesPagedAsync_SecondPage_SkipsCorrectly()
    {
        var ids = new List<string>();
        var baseTime = DateTime.UtcNow.AddHours(-10);
        for (var i = 0; i < 10; i++)
        {
            var img = MakeImage() with { GeneratedAt = baseTime.AddMinutes(i) };
            ids.Add(img.Id);
            await _repo.SaveImageAsync(img);
        }

        var page = await _repo.LoadImagesPagedAsync(skip: 3, take: 3);

        page.Should().HaveCount(3);
        // Skip 3 nejnovějších → vrátí i=6, 5, 4
        page[0].Id.Should().Be(ids[6]);
        page[1].Id.Should().Be(ids[5]);
        page[2].Id.Should().Be(ids[4]);
    }

    [Fact]
    public async Task LoadImagesPagedAsync_BeyondEnd_ReturnsEmpty()
    {
        await _repo.SaveImageAsync(MakeImage());
        await _repo.SaveImageAsync(MakeImage());

        var page = await _repo.LoadImagesPagedAsync(skip: 100, take: 50);

        page.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadImagesPagedAsync_InvalidArgs_HandledGracefully()
    {
        await _repo.SaveImageAsync(MakeImage());

        var noTake = await _repo.LoadImagesPagedAsync(skip: 0, take: 0);
        noTake.Should().BeEmpty();

        var negativeSkip = await _repo.LoadImagesPagedAsync(skip: -5, take: 10);
        negativeSkip.Should().HaveCount(1, "negativní skip se normalizuje na 0");
    }

    // ── Filtrování ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Filter_BySearch_MatchesPromptAndModel()
    {
        await _repo.SaveImageAsync(MakeImage() with { Prompt = "rudý drak nad horami", ModelName = "flux-dev" });
        await _repo.SaveImageAsync(MakeImage() with { Prompt = "modrá kočka",          ModelName = "sdxl-base" });
        await _repo.SaveImageAsync(MakeImage() with { Prompt = "klidná krajina",       ModelName = "drak-model" });

        // hledá v promptu i v modelu → "drak" matchne 2 záznamy
        var filter = new ImageQueryFilter(Search: "drak");
        (await _repo.CountImagesAsync(filter)).Should().Be(2);
        var page = await _repo.LoadImagesPagedAsync(0, 50, filter);
        page.Should().HaveCount(2);
    }

    [Fact]
    public async Task Filter_ByModel_ExactMatchOnly()
    {
        await _repo.SaveImageAsync(MakeImage() with { ModelName = "flux-dev" });
        await _repo.SaveImageAsync(MakeImage() with { ModelName = "flux-dev" });
        await _repo.SaveImageAsync(MakeImage() with { ModelName = "sdxl-base" });

        var filter = new ImageQueryFilter(ModelName: "flux-dev");
        (await _repo.CountImagesAsync(filter)).Should().Be(2);
        (await _repo.LoadImagesPagedAsync(0, 50, filter)).Should().OnlyContain(r => r.ModelName == "flux-dev");
    }

    [Fact]
    public async Task Filter_SearchAndModel_CombinedWithAnd()
    {
        await _repo.SaveImageAsync(MakeImage() with { Prompt = "drak", ModelName = "flux-dev" });
        await _repo.SaveImageAsync(MakeImage() with { Prompt = "drak", ModelName = "sdxl-base" });
        await _repo.SaveImageAsync(MakeImage() with { Prompt = "kočka", ModelName = "flux-dev" });

        var filter = new ImageQueryFilter(Search: "drak", ModelName: "flux-dev");
        (await _repo.CountImagesAsync(filter)).Should().Be(1);
    }

    [Fact]
    public async Task Filter_EmptyFilter_ReturnsAll()
    {
        for (var i = 0; i < 4; i++) await _repo.SaveImageAsync(MakeImage());

        (await _repo.CountImagesAsync(ImageQueryFilter.None)).Should().Be(4);
        (await _repo.CountImagesAsync(null)).Should().Be(4);
    }

    [Fact]
    public async Task GetDistinctModels_ReturnsUniqueSortedNonEmpty()
    {
        await _repo.SaveImageAsync(MakeImage() with { ModelName = "sdxl-base" });
        await _repo.SaveImageAsync(MakeImage() with { ModelName = "flux-dev" });
        await _repo.SaveImageAsync(MakeImage() with { ModelName = "flux-dev" });
        await _repo.SaveImageAsync(MakeImage() with { ModelName = "" });

        var models = await _repo.GetDistinctModelsAsync();

        models.Should().Equal("flux-dev", "sdxl-base"); // unikátní, bez prázdných, abecedně
    }
}
