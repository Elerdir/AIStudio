using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Core.Services;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

public class CuratedImageModelRecommenderTests
{
    private readonly IImageModelMatcher _matcher = Substitute.For<IImageModelMatcher>();

    private CuratedImageModelRecommender MakeRecommender() => new(_matcher);

    private static ImageIntent IntentOf(ImageKind kind) => new(
        Kind:           kind,
        Aspect:         ImageAspect.Square,
        Quality:        ImageQualityHint.Normal,
        EnglishPrompt:  "test prompt",
        NegativePrompt: "",
        Reasoning:      "test");

    // ── No-upgrade scénáře ────────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_AnimeKind_UserHasAnimagine_NoUpgrade()
    {
        // Anime má curated picky (Animagine XL 3.1). Pokud uživatel ho má → no upgrade
        _matcher.Match(ImageKind.Anime, Arg.Any<IReadOnlyList<string>>())
                .Returns(RecommendedModels.AnimagineXl31.FileName);

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Anime),
            new[] { RecommendedModels.AnimagineXl31.FileName },
            CancellationToken.None);

        rec.Upgrade.Should().BeNull("user už má Animagine XL");
    }

    [Fact]
    public async Task Recommend_AnimeKind_UserHasNoAnimeModel_OffersAnimagine()
    {
        _matcher.Match(ImageKind.Anime, Arg.Any<IReadOnlyList<string>>()).Returns("sd15.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Anime),
            new[] { "sd15.safetensors" },
            CancellationToken.None);

        rec.Upgrade.Should().NotBeNull();
        rec.Upgrade!.Id.Should().Be(RecommendedModels.AnimagineXl31.Id);
    }

    [Fact]
    public async Task Recommend_UserAlreadyHasIdealByExactName_ReturnsNoUpgrade()
    {
        // Realistic ideal po rozšíření katalogu = SDXL Base 1.0
        var ideal = RecommendedModels.SdxlBase10;
        _matcher.Match(ImageKind.Realistic, Arg.Any<IReadOnlyList<string>>()).Returns(ideal.FileName);

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Realistic),
            new[] { ideal.FileName },
            CancellationToken.None);

        rec.Upgrade.Should().BeNull("uživatel už má ideal model lokálně");
    }

    [Fact]
    public async Task Recommend_UserHasDifferentQuantization_StillOffersUpgrade()
    {
        // Stejný stem ale jiná kvantizace — flux1-schnell-Q5_K_M.gguf vs Q4_0
        // Aktuální heuristika je stem-contains — striktnější (Q5 != Q4), takže
        // recommender nabídne Q4. Pokud se v budoucnu změní na liberálnější
        // match (jen base name "flux1-schnell"), tenhle test by se měl změnit.
        var checkpoints = new[] { "flux1-schnell-Q5_K_M.gguf" };
        _matcher.Match(ImageKind.Abstract, Arg.Any<IReadOnlyList<string>>()).Returns(checkpoints[0]);

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Abstract),
            checkpoints,
            CancellationToken.None);

        rec.Upgrade.Should().NotBeNull("Q5 a Q4 jsou odlišné varianty — recommender je nepovažuje za stejný");
    }

    [Fact]
    public async Task Recommend_StylizedKind_UserHasFluxOrXl_OffersDreamShaperXl()
    {
        // Po rozšíření katalogu Stylized ideál = DreamShaper XL Lightning (SDXL tier).
        // Sanity guard (downgrade prevention proti SD 1.5 DreamShaper) byl pro
        // starší verzi — DreamShaper XL je stejný tier jako uživatelova SDXL,
        // takže nabídka je legitimní (jen jiný styl).
        _matcher.Match(ImageKind.Stylized, Arg.Any<IReadOnlyList<string>>()).Returns("sd_xl_base_1.0.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Stylized),
            new[] { "sd_xl_base_1.0.safetensors" },
            CancellationToken.None);

        // User má jiný XL → recommender nabídne DreamShaper XL pro stylizovaný styl
        rec.Upgrade.Should().NotBeNull();
        rec.Upgrade!.Id.Should().Be(RecommendedModels.DreamShaperXl_Lightning.Id);
    }

    // ── Upgrade scénáře ───────────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_RealisticKind_UserHasOnlySd15_OffersSdxlBase()
    {
        _matcher.Match(ImageKind.Realistic, Arg.Any<IReadOnlyList<string>>()).Returns("DreamShaper_8_pruned.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Realistic),
            new[] { "DreamShaper_8_pruned.safetensors" },
            CancellationToken.None);

        rec.Upgrade.Should().NotBeNull();
        rec.Upgrade!.Id.Should().Be(RecommendedModels.SdxlBase10.Id);
        rec.Upgrade.Reason.Should().NotBeNullOrEmpty();
        rec.LocalBestMatch.Should().Be("DreamShaper_8_pruned.safetensors", "lokální match je furt vrácen pro 'use local' branch");
    }

    [Fact]
    public async Task Recommend_EmptyLocalCheckpoints_OffersIdeal()
    {
        // Uživatel nemá vůbec nic — recommender nabídne ideal pro kind
        _matcher.Match(ImageKind.Realistic, Arg.Any<IReadOnlyList<string>>()).Returns((string?)null);

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Realistic),
            Array.Empty<string>(),
            CancellationToken.None);

        rec.LocalBestMatch.Should().BeNull();
        rec.Upgrade.Should().NotBeNull();
        rec.Upgrade!.Name.Should().Contain("XL", "Realistic ideal po rozšíření = SDXL Base 1.0");
    }

    [Fact]
    public async Task Recommend_OfferContainsAllMetadataForDownload()
    {
        _matcher.Match(ImageKind.Abstract, Arg.Any<IReadOnlyList<string>>()).Returns("sd15.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Abstract),
            new[] { "sd15.safetensors" },
            CancellationToken.None);

        rec.Upgrade.Should().NotBeNull();
        var o = rec.Upgrade!;
        o.DownloadUrl.Should().StartWith("https://");
        o.FileName.Should().NotBeNullOrEmpty();
        o.SizeBytes.Should().BeGreaterThan(0);
        o.Reason.Should().NotBeNullOrEmpty();
    }
}
