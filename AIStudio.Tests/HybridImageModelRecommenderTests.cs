using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AIStudio.Tests;

public class HybridImageModelRecommenderTests
{
    private readonly IImageModelRecommender _curated   = Substitute.For<IImageModelRecommender>();
    private readonly IModelDiscoveryService _discovery = Substitute.For<IModelDiscoveryService>();

    private HybridImageModelRecommender MakeHybrid() => new(_curated, _discovery);

    private static ImageIntent Intent(ImageKind kind) => new(
        Kind:           kind,
        Aspect:         ImageAspect.Square,
        Quality:        ImageQualityHint.Normal,
        EnglishPrompt:  "test",
        NegativePrompt: "",
        Reasoning:      "test");

    private static DiscoveredModel MakeDiscovered(
        string filename  = "JuggernautXL_v9.safetensors",
        long   downloads = 100_000,
        string provider  = "Civitai") => new(
            Provider:     provider,
            ProviderRef:  "12345",
            Name:         "Juggernaut XL v9",
            Author:       "RunDiffusion",
            Description:  "Top-tier realistic SDXL",
            FileName:     filename,
            DownloadUrl:  "https://civitai.com/api/download/models/12345?token=xxx",
            ModelPageUrl: "https://civitai.com/models/12345",
            SizeBytes:    6_900_000_000L,
            Downloads:    downloads,
            Rating:       4.8,
            Nsfw:         false,
            ThumbnailUrl: null,
            BaseModel:    "SDXL 1.0",
            FileFormat:   "SafeTensor",
            Sha256:       "deadbeef");

    // ── Curated má prioritu ──────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_CuratedReturnsUpgrade_PassesItThroughAndDoesNotCallDiscovery()
    {
        var curatedOffer = new ModelUpgradeOffer(
            "curated-id", "Curated Model", "curated reason", 1_000_000_000,
            "https://hf/x", "curated.safetensors", null, false);
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation("local.safetensors", curatedOffer));

        var result = await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Realistic), new[] { "local.safetensors" }, CancellationToken.None);

        result.Upgrade.Should().BeSameAs(curatedOffer);
        // Live search se vůbec nevolal — curated má prioritu
        await _discovery.DidNotReceive().SearchAsync(
            Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Curated nemá upgrade ale user má local → no upgrade (live se nevolá) ─

    [Fact]
    public async Task Recommend_NoUpgradeButHasLocal_DoesNotInvokeLiveSearch()
    {
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation("sd_xl.safetensors", Upgrade: null));

        var result = await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Realistic), new[] { "sd_xl.safetensors" }, CancellationToken.None);

        result.Upgrade.Should().BeNull();
        result.LocalBestMatch.Should().Be("sd_xl.safetensors");
        await _discovery.DidNotReceive().SearchAsync(
            Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Curated nic + no local → live search vrátí offer ─────────────────────

    [Fact]
    public async Task Recommend_NoCuratedNoLocal_LiveSearchFindsTopModel()
    {
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation(LocalBestMatch: null, Upgrade: null));

        var top    = MakeDiscovered(downloads: 200_000);
        var less   = MakeDiscovered("OtherModel.safetensors", downloads: 50_000);
        _discovery.SearchAsync(Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
                               Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(new[] { less, top });  // úmyslně neuspořádané

        var result = await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Anime), Array.Empty<string>(), CancellationToken.None);

        result.Upgrade.Should().NotBeNull();
        result.Upgrade!.Name.Should().Be(top.Name, "vybírá se top podle downloads, ne první z listu");
        result.Upgrade.FileName.Should().Be(top.FileName);
        result.Upgrade.DownloadUrl.Should().Be(top.DownloadUrl);
        result.Upgrade.Sha256.Should().Be(top.Sha256);
        result.Upgrade.Id.Should().StartWith("live-");
        result.Upgrade.Reason.Should().Contain("anime");  // kind label
    }

    [Fact]
    public async Task Recommend_LiveSearchReturnsEmpty_NoUpgrade()
    {
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation(LocalBestMatch: null, Upgrade: null));
        _discovery.SearchAsync(Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
                               Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(Array.Empty<DiscoveredModel>());

        var result = await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Anime), Array.Empty<string>(), CancellationToken.None);

        result.Upgrade.Should().BeNull();
    }

    [Fact]
    public async Task Recommend_LiveSearchReturnsModelUserAlreadyHas_NoUpgrade()
    {
        // I když live něco našlo, pokud user ten konkrétní filename už má, ignorujeme.
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation(LocalBestMatch: null, Upgrade: null));

        var top = MakeDiscovered(filename: "ALREADY_HAVE.safetensors");
        _discovery.SearchAsync(Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
                               Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(new[] { top });

        var result = await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Anime),
            new[] { "ALREADY_HAVE.safetensors" },
            CancellationToken.None);

        result.Upgrade.Should().BeNull();
    }

    [Fact]
    public async Task Recommend_LiveSearchThrows_FallsBackGracefullyWithNoUpgrade()
    {
        // Network failure během search → recommender nesmí celé generování shodit
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation(LocalBestMatch: null, Upgrade: null));
        _discovery.SearchAsync(Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
                               Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Throws(new HttpRequestException("offline"));

        var result = await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Anime), Array.Empty<string>(), CancellationToken.None);

        result.Upgrade.Should().BeNull("síťová chyba při live search nesmí blokovat generování");
    }

    [Fact]
    public async Task Recommend_CancellationDuringLiveSearch_Propagates()
    {
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation(LocalBestMatch: null, Upgrade: null));
        _discovery.SearchAsync(Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
                               Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Throws(new OperationCanceledException());

        var act = async () => await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Anime), Array.Empty<string>(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "Stop tlačítko musí proletět skrz, ne se skrýt za generic catch");
    }

    // ── Provider mapping pro různé kindy ──────────────────────────────────────

    [Theory]
    [InlineData(ImageKind.Realistic, PickProvider.Civitai)]
    [InlineData(ImageKind.Anime,     PickProvider.Civitai)]
    [InlineData(ImageKind.Stylized,  PickProvider.Civitai)]
    [InlineData(ImageKind.Abstract,  PickProvider.HuggingFace)]
    [InlineData(ImageKind.Auto,      PickProvider.HuggingFace)]
    public async Task Recommend_DispatchesToCorrectProviderForKind(ImageKind kind, PickProvider expectedProvider)
    {
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation(LocalBestMatch: null, Upgrade: null));
        _discovery.SearchAsync(Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
                               Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(Array.Empty<DiscoveredModel>());

        await MakeHybrid().RecommendAsync(Intent(kind), Array.Empty<string>(), CancellationToken.None);

        await _discovery.Received(1).SearchAsync(
            expectedProvider,
            Arg.Any<string>(),
            PickKind.Image,
            includeNsfw: false,
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recommend_HuggingFaceProvider_OfferRequiresHfToken()
    {
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation(LocalBestMatch: null, Upgrade: null));
        var hfModel = MakeDiscovered(provider: "HuggingFace");
        _discovery.SearchAsync(Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
                               Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(new[] { hfModel });

        var result = await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Abstract), Array.Empty<string>(), CancellationToken.None);

        result.Upgrade!.RequiresHuggingFaceToken.Should().BeTrue("HF gated repa potřebují token");
    }

    [Fact]
    public async Task Recommend_CivitaiProvider_DoesNotRequireHfToken()
    {
        _curated.RecommendAsync(Arg.Any<ImageIntent>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ImageModelRecommendation(LocalBestMatch: null, Upgrade: null));
        var civitai = MakeDiscovered(provider: "Civitai");
        _discovery.SearchAsync(Arg.Any<PickProvider>(), Arg.Any<string>(), Arg.Any<PickKind?>(),
                               Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(new[] { civitai });

        var result = await MakeHybrid().RecommendAsync(
            Intent(ImageKind.Anime), Array.Empty<string>(), CancellationToken.None);

        result.Upgrade!.RequiresHuggingFaceToken.Should().BeFalse();
    }
}
