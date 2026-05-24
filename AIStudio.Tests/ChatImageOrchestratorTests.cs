using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AIStudio.Tests;

/// <summary>
/// Unit testy pro <see cref="ChatImageOrchestrator"/> — orchestrátor mezi
/// intent parserem, model matcherem, ComfyUI klientem a SQLite galerií.
///
/// <para>Strategie: všechny závislosti jsou mockované přes NSubstitute,
/// disk operace jdou do dočasné složky (uklízí se v Dispose). Pro test
/// použijeme internal ctor s outputDirOverride, ať se obrázky neukládají
/// do reálné uživatelské galerie.</para>
/// </summary>
public class ChatImageOrchestratorTests : IDisposable
{
    private readonly string                     _tmpDir;
    private readonly IImageIntentParser         _parser   = Substitute.For<IImageIntentParser>();
    private readonly IImageModelMatcher         _matcher  = Substitute.For<IImageModelMatcher>();
    private readonly IComfyService              _comfy    = Substitute.For<IComfyService>();
    private readonly IImageRepository           _repo     = Substitute.For<IImageRepository>();
    private readonly ISettingsService           _settings = Substitute.For<ISettingsService>();

    public ChatImageOrchestratorTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "AIStudio.Tests.Orch", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private ChatImageOrchestrator MakeOrchestrator() =>
        new(_parser, _matcher, _comfy, _repo, _settings, outputDirOverride: _tmpDir);

    /// <summary>
    /// Připraví "rozumný" happy-path mock: ComfyUI běží, intent parser vrátí
    /// realistic kind, jeden checkpoint je k dispozici, matcher ho vrátí,
    /// queue + wait projde, download vrátí trochu bytů (validní PNG hlavička).
    /// </summary>
    private void SetupHappyPath(string modelName = "sd_xl_base_1.0.safetensors")
    {
        _comfy.IsRunning.Returns(true);
        _parser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new ImageIntent(
                   Kind:           ImageKind.Realistic,
                   Aspect:         ImageAspect.Square,
                   Quality:        ImageQualityHint.Normal,
                   EnglishPrompt:  "a cat on a roof",
                   NegativePrompt: "blurry, low quality",
                   Reasoning:      "realistic foto scene"));
        _comfy.GetCheckpointsAsync(Arg.Any<CancellationToken>())
              .Returns(new[] { modelName });
        _matcher.Match(Arg.Any<ImageKind>(), Arg.Any<IReadOnlyList<string>>()).Returns(modelName);
        _comfy.QueuePromptAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
              .Returns("test-prompt-id");
        _comfy.WaitForResultAsync(Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>())
              .Returns(new ComfyGenerationResult(
                   PromptId:     "test-prompt-id",
                   Images:       new[] { new ComfyImageRef("out.png", "", "output") },
                   CompletedAt:  DateTime.Now));
        // Validní 1×1 transparentní PNG (89 bytes), aby File.WriteAllBytes uložilo něco rozumného
        _comfy.DownloadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(MinimalPngBytes());
    }

    private static byte[] MinimalPngBytes() => new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR length + tag
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1×1
        0x08, 0x06, 0x00, 0x00, 0x00,                   // 8-bit RGBA
        0x1F, 0x15, 0xC4, 0x89,                         // IHDR CRC
    };

    // ─────────────────────────────────────────────────────────────────────────
    //   Validační chyby (rychlý fail před voláním Comfy)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_NoCheckpoints_ReturnsFailWithHint()
    {
        _comfy.IsRunning.Returns(true);
        _parser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new ImageIntent(ImageKind.Auto, ImageAspect.Square, ImageQualityHint.Normal,
                                        "x", "", "test"));
        _comfy.GetCheckpointsAsync(Arg.Any<CancellationToken>())
              .Returns(Array.Empty<string>());

        var result = await MakeOrchestrator().GenerateAsync("nakresli kočku", null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("checkpoint");
    }

    [Fact]
    public async Task GenerateAsync_MatcherReturnsNull_ReturnsFail()
    {
        _comfy.IsRunning.Returns(true);
        _parser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new ImageIntent(ImageKind.Anime, ImageAspect.Square, ImageQualityHint.Normal,
                                        "x", "", "anime"));
        _comfy.GetCheckpointsAsync(Arg.Any<CancellationToken>())
              .Returns(new[] { "sd15.safetensors" });
        _matcher.Match(Arg.Any<ImageKind>(), Arg.Any<IReadOnlyList<string>>()).Returns((string?)null);

        var result = await MakeOrchestrator().GenerateAsync("anime postava", null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("vhodný model");
    }

    [Fact]
    public async Task GenerateAsync_GgufModel_ReturnsFailWithHint()
    {
        SetupHappyPath(modelName: "flux1-schnell-Q4_K_S.gguf");

        var result = await MakeOrchestrator().GenerateAsync("něco", null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("GGUF");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //   ComfyUI lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_ComfyNotRunning_StartsItFirst()
    {
        _comfy.IsRunning.Returns(false);
        _comfy.StartAsync(Arg.Any<CancellationToken>()).Returns(true);
        SetupComfyAfterStart();

        var result = await MakeOrchestrator().GenerateAsync("nakresli kočku", null, null, CancellationToken.None);

        await _comfy.Received(1).StartAsync(Arg.Any<CancellationToken>());
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_ComfyStartFails_ReturnsFail()
    {
        _comfy.IsRunning.Returns(false);
        _comfy.StartAsync(Arg.Any<CancellationToken>()).Returns(false);

        var result = await MakeOrchestrator().GenerateAsync("něco", null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ComfyUI");
        // Parser by se neměl vůbec zavolat — bail-out před parsováním
        await _parser.DidNotReceive().ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private void SetupComfyAfterStart()
    {
        // Stejný setup jako happy path, jen IsRunning byl false a po StartAsync zůstal mock OK
        _parser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new ImageIntent(ImageKind.Realistic, ImageAspect.Square, ImageQualityHint.Normal,
                                        "a cat", "", "test"));
        _comfy.GetCheckpointsAsync(Arg.Any<CancellationToken>())
              .Returns(new[] { "sd_xl_base_1.0.safetensors" });
        _matcher.Match(Arg.Any<ImageKind>(), Arg.Any<IReadOnlyList<string>>())
                .Returns("sd_xl_base_1.0.safetensors");
        _comfy.QueuePromptAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
              .Returns("pid");
        _comfy.WaitForResultAsync(Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>())
              .Returns(new ComfyGenerationResult("pid", new[] { new ComfyImageRef("out.png", "", "output") }, DateTime.Now));
        _comfy.DownloadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(MinimalPngBytes());
    }

    // ─────────────────────────────────────────────────────────────────────────
    //   Happy path
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_HappyPath_WritesFile_SavesToRepo_ReturnsSuccess()
    {
        SetupHappyPath();

        var result = await MakeOrchestrator().GenerateAsync("nakresli kočku", null, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ImagePath.Should().NotBeNullOrEmpty();
        File.Exists(result.ImagePath!).Should().BeTrue("orchestrátor má soubor zapsat na disk");
        result.ModelUsed.Should().Be("sd_xl_base_1.0.safetensors");
        result.EnglishPrompt.Should().Be("a cat on a roof");
        result.Reasoning.Should().Be("realistic foto scene");
        result.Width.Should().Be(1024);
        result.Height.Should().Be(1024);
        await _repo.Received(1).SaveImageAsync(Arg.Is<ImageRecord>(r =>
            r.FilePath == result.ImagePath &&
            r.ModelName == "sd_xl_base_1.0.safetensors"));
    }

    [Fact]
    public async Task GenerateAsync_LandscapeAspect_UsesWideResolution()
    {
        _comfy.IsRunning.Returns(true);
        _parser.ParseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(new ImageIntent(ImageKind.Realistic, ImageAspect.Landscape, ImageQualityHint.Normal,
                                        "wide scene", "", "krajina"));
        _comfy.GetCheckpointsAsync(Arg.Any<CancellationToken>()).Returns(new[] { "sd.safetensors" });
        _matcher.Match(Arg.Any<ImageKind>(), Arg.Any<IReadOnlyList<string>>()).Returns("sd.safetensors");
        _comfy.QueuePromptAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>()).Returns("pid");
        _comfy.WaitForResultAsync(Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>())
              .Returns(new ComfyGenerationResult("pid", new[] { new ComfyImageRef("o.png", "", "output") }, DateTime.Now));
        _comfy.DownloadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(MinimalPngBytes());

        var result = await MakeOrchestrator().GenerateAsync("krajinu", null, null, CancellationToken.None);

        result.Width.Should().BeGreaterThan(result.Height, "landscape má být širší než vyšší");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //   Img2img s referencí
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WithExistingReference_UploadsAndUsesIt()
    {
        SetupHappyPath();
        _comfy.UploadImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("uploaded_ref.png");

        var refPath = Path.Combine(_tmpDir, "input_ref.png");
        await File.WriteAllBytesAsync(refPath, MinimalPngBytes());

        var result = await MakeOrchestrator().GenerateAsync("v noci", refPath, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        await _comfy.Received(1).UploadImageAsync(refPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_MissingReferenceFile_FallsBackToTxt2Img()
    {
        SetupHappyPath();

        var nonexistent = Path.Combine(_tmpDir, "does-not-exist.png");

        var result = await MakeOrchestrator().GenerateAsync("něco", nonexistent, null, CancellationToken.None);

        // Chybějící reference se nemá uploadovat ani crashnout — jen padne na txt2img
        result.Success.Should().BeTrue();
        await _comfy.DidNotReceive().UploadImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_UploadFails_FallsBackToTxt2Img()
    {
        SetupHappyPath();
        _comfy.UploadImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Throws(new InvalidOperationException("upload failed"));

        var refPath = Path.Combine(_tmpDir, "input_ref.png");
        await File.WriteAllBytesAsync(refPath, MinimalPngBytes());

        var result = await MakeOrchestrator().GenerateAsync("v noci", refPath, null, CancellationToken.None);

        // I když upload selhal, generování pokračuje jako txt2img (rozumný degradation)
        result.Success.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //   Selhání Comfy
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_WaitForResultReturnsNull_ReturnsFail()
    {
        SetupHappyPath();
        _comfy.WaitForResultAsync(Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>())
              .Returns((ComfyGenerationResult?)null);

        var result = await MakeOrchestrator().GenerateAsync("něco", null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GenerateAsync_WaitForResultEmptyImages_ReturnsFail()
    {
        SetupHappyPath();
        _comfy.WaitForResultAsync(Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>())
              .Returns(new ComfyGenerationResult("pid", Array.Empty<ComfyImageRef>(), DateTime.Now));

        var result = await MakeOrchestrator().GenerateAsync("něco", null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_ComfyQueueThrows_ReturnsFail()
    {
        SetupHappyPath();
        _comfy.QueuePromptAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
              .Throws(new HttpRequestException("connection refused"));

        var result = await MakeOrchestrator().GenerateAsync("něco", null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Chyba");
    }

    [Fact]
    public async Task GenerateAsync_Cancelled_ReturnsZruseno()
    {
        SetupHappyPath();
        _comfy.WaitForResultAsync(Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>())
              .Throws(new OperationCanceledException());

        var result = await MakeOrchestrator().GenerateAsync("něco", null, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ru");  // "Zrušeno" / "zrušeno"
    }

    // ─────────────────────────────────────────────────────────────────────────
    //   Robustnost
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_RepoSaveThrows_StillReturnsSuccess()
    {
        SetupHappyPath();
        _repo.SaveImageAsync(Arg.Any<ImageRecord>())
             .Throws(new InvalidOperationException("db locked"));

        var result = await MakeOrchestrator().GenerateAsync("něco", null, null, CancellationToken.None);

        // Soubor je na disku, jen není v galerii — best-effort save nesmí shodit flow
        result.Success.Should().BeTrue();
        File.Exists(result.ImagePath!).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_FileNameContainsChatPrefix()
    {
        // Pro odlišení od obrázků vygenerovaných v Image Studiu — uživatel pozná
        // původ podle prefixu v file jméně.
        SetupHappyPath();

        var result = await MakeOrchestrator().GenerateAsync("něco", null, null, CancellationToken.None);

        Path.GetFileName(result.ImagePath!).Should().Contain("chat");
    }
}
