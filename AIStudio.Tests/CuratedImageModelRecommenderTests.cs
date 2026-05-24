using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
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
    public async Task Recommend_AnimeKind_NoCuratedPick_ReturnsNoUpgrade()
    {
        // Anime nemá v katalogu curated picky → recommender vrátí no upgrade
        _matcher.Match(ImageKind.Anime, Arg.Any<IReadOnlyList<string>>()).Returns("sd15.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Anime),
            new[] { "sd15.safetensors" },
            CancellationToken.None);

        rec.Upgrade.Should().BeNull("Anime nemá v katalogu curated model");
        rec.LocalBestMatch.Should().Be("sd15.safetensors");
    }

    [Fact]
    public async Task Recommend_UserAlreadyHasIdealByExactName_ReturnsNoUpgrade()
    {
        var ideal = RecommendedModels.FluxSchnell_Q4;
        _matcher.Match(ImageKind.Realistic, Arg.Any<IReadOnlyList<string>>()).Returns(ideal.FileName);

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Realistic),
            new[] { ideal.FileName },
            CancellationToken.None);

        rec.Upgrade.Should().BeNull("uživatel už má ideal model lokálně");
    }

    [Fact]
    public async Task Recommend_UserHasIdealByStemMatch_ReturnsNoUpgrade()
    {
        // Stejný stem ale jiná kvantizace — flux1-schnell-Q5_K_M.gguf vs Q4_0
        var checkpoints = new[] { "flux1-schnell-Q5_K_M.gguf" };
        _matcher.Match(ImageKind.Abstract, Arg.Any<IReadOnlyList<string>>()).Returns(checkpoints[0]);

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Abstract),
            checkpoints,
            CancellationToken.None);

        // FluxSchnell stem ("flux1-schnell-Q4_0") obsahuje "flux1-schnell" — match by substring
        // by měl projít i pro jinou kvantizaci. Pokud FluxSchnell.FileName je „flux1-schnell-Q4_0.gguf",
        // stem je "flux1-schnell-Q4_0" který se neobjeví v Q5 souboru.
        // POZOR: aktuální heuristika je stem-contains — striktnější. Tenhle test ověří, že
        // varianty se neoznačí jako duplicate (Q5 != Q4, recommender nabídne upgrade Q4).
        // Pokud se v budoucnu změní na liberálnější match (jen base name "flux1-schnell"),
        // tenhle test by se měl změnit.
        rec.Upgrade.Should().NotBeNull("Q5 a Q4 jsou odlišné varianty — recommender je nepovažuje za stejný");
    }

    [Fact]
    public async Task Recommend_StylizedKind_UserHasSdxl_DoesNotSuggestDreamShaperDowngrade()
    {
        // Sanity guard: Stylized recommender by chtěl DreamShaper SD1.5, ale
        // pokud uživatel má SDXL nebo FLUX, downgrade by byl nesmysl.
        _matcher.Match(ImageKind.Stylized, Arg.Any<IReadOnlyList<string>>()).Returns("sd_xl_base_1.0.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Stylized),
            new[] { "sd_xl_base_1.0.safetensors" },
            CancellationToken.None);

        rec.Upgrade.Should().BeNull("XL model je vyšší tier než SD 1.5 DreamShaper — žádný downgrade");
    }

    [Fact]
    public async Task Recommend_StylizedKind_UserHasFlux_DoesNotSuggestDreamShaperDowngrade()
    {
        _matcher.Match(ImageKind.Stylized, Arg.Any<IReadOnlyList<string>>()).Returns("flux1-dev-fp8.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Stylized),
            new[] { "flux1-dev-fp8.safetensors" },
            CancellationToken.None);

        rec.Upgrade.Should().BeNull("FLUX je výrazně vyšší tier než DreamShaper SD 1.5");
    }

    // ── Upgrade scénáře ───────────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_RealisticKind_UserHasOnlySd15_OffersFluxSchnell()
    {
        _matcher.Match(ImageKind.Realistic, Arg.Any<IReadOnlyList<string>>()).Returns("DreamShaper_8_pruned.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Realistic),
            new[] { "DreamShaper_8_pruned.safetensors" },
            CancellationToken.None);

        rec.Upgrade.Should().NotBeNull();
        rec.Upgrade!.Id.Should().Be(RecommendedModels.FluxSchnell_Q4.Id);
        rec.Upgrade.Reason.Should().NotBeNullOrEmpty();
        rec.LocalBestMatch.Should().Be("DreamShaper_8_pruned.safetensors", "lokální match je furt vrácen pro 'use local' branch");
    }

    [Fact]
    public async Task Recommend_StylizedKind_UserHasOnlySd15Other_OffersDreamShaper()
    {
        // Uživatel má SD 1.5 ale ne přímo DreamShaper a žádný XL/FLUX
        _matcher.Match(ImageKind.Stylized, Arg.Any<IReadOnlyList<string>>()).Returns("realistic-vision-v5.safetensors");

        var rec = await MakeRecommender().RecommendAsync(
            IntentOf(ImageKind.Stylized),
            new[] { "realistic-vision-v5.safetensors" },
            CancellationToken.None);

        rec.Upgrade.Should().NotBeNull();
        rec.Upgrade!.Id.Should().Be(RecommendedModels.DreamShaper8_Sd15.Id);
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
        rec.Upgrade!.Name.Should().Contain("FLUX");
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
