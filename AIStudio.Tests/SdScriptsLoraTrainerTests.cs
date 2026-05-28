using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Testy pro pure helpers v <see cref="SdScriptsLoraTrainer"/>.
/// Subprocess + Python část se přímo netestuje (vyžadovala by Python prostředí).
/// </summary>
public class SdScriptsLoraTrainerTests
{
    // ── ValidateRequest: detekce neplatných configů ───────────────────────────

    [Fact]
    public void ValidateRequest_EmptyName_ReturnsError()
    {
        var req = MakeRequest(name: "");
        var error = SdScriptsLoraTrainer.ValidateRequest(req);
        error.Should().NotBeNull().And.Contain("Název");
    }

    [Theory]
    [InlineData("invalid/name")]
    [InlineData("name<with>brackets")]
    [InlineData("name|pipe")]
    public void ValidateRequest_InvalidFilenameChars_ReturnsError(string name)
    {
        var req = MakeRequest(name: name);
        var error = SdScriptsLoraTrainer.ValidateRequest(req);
        error.Should().NotBeNull().And.Contain("znaky");
    }

    [Fact]
    public void ValidateRequest_NonExistentBaseModel_ReturnsError()
    {
        var req = MakeRequest(baseModelPath: @"C:\nonexistent\model.safetensors");
        var error = SdScriptsLoraTrainer.ValidateRequest(req);
        error.Should().NotBeNull().And.Contain("Základní model neexistuje");
    }

    [Fact]
    public void ValidateRequest_TooFewImages_ReturnsError()
    {
        var (baseModel, _) = CreateTempBaseModel();
        try
        {
            var req = MakeRequest(baseModelPath: baseModel, datasetSize: 3);
            var error = SdScriptsLoraTrainer.ValidateRequest(req);
            error.Should().NotBeNull().And.Contain("Příliš málo");
        }
        finally { File.Delete(baseModel); }
    }

    [Fact]
    public void ValidateRequest_TooManyImages_ReturnsError()
    {
        var (baseModel, images) = CreateTempBaseModel(datasetSize: 250);
        try
        {
            var req = MakeRequest(baseModelPath: baseModel,
                                  datasetSize: 250,
                                  imageFiles: images);
            var error = SdScriptsLoraTrainer.ValidateRequest(req);
            error.Should().NotBeNull().And.Contain("Příliš mnoho");
        }
        finally
        {
            File.Delete(baseModel);
            foreach (var f in images) try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void ValidateRequest_ValidConfig_ReturnsNull()
    {
        var (baseModel, images) = CreateTempBaseModel(datasetSize: 20);
        try
        {
            var req = MakeRequest(baseModelPath: baseModel,
                                  datasetSize: 20,
                                  imageFiles: images);
            var error = SdScriptsLoraTrainer.ValidateRequest(req);
            error.Should().BeNull("validní config nemá vrátit chybu");
        }
        finally
        {
            File.Delete(baseModel);
            foreach (var f in images) try { File.Delete(f); } catch { }
        }
    }

    // ── TryParseProgressLine: parse tqdm output z sd-scripts ──────────────────

    [Fact]
    public void TryParseProgressLine_ValidTqdmLine_ExtractsStepAndLoss()
    {
        const string line = "steps:  35%|████      | 525/1500 [02:14<04:09, 3.91it/s, avr_loss=0.1234]";

        var ok = SdScriptsLoraTrainer.TryParseProgressLine(line, out var step, out var total, out var loss);

        ok.Should().BeTrue();
        step.Should().Be(525);
        total.Should().Be(1500);
        loss.Should().NotBeNull().And.BeApproximately(0.1234, 1e-6);
    }

    [Fact]
    public void TryParseProgressLine_LineWithoutLoss_StillParses()
    {
        const string line = "steps:   5%|▌         | 75/1500 [00:20<06:30, 3.65it/s]";

        var ok = SdScriptsLoraTrainer.TryParseProgressLine(line, out var step, out var total, out var loss);

        ok.Should().BeTrue();
        step.Should().Be(75);
        total.Should().Be(1500);
        loss.Should().BeNull("řádek bez avr_loss má vrátit null");
    }

    [Theory]
    [InlineData("loading checkpoint...")]
    [InlineData("INFO: training started")]
    [InlineData("WARNING: VRAM allocation failed")]
    [InlineData("")]
    [InlineData("steps: not a progress line")]
    public void TryParseProgressLine_NonProgressLine_ReturnsFalse(string line)
    {
        var ok = SdScriptsLoraTrainer.TryParseProgressLine(line, out _, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseProgressLine_ScientificNotationLoss_Parses()
    {
        const string line = "steps:  10%|█         | 150/1500 [00:40<06:15, 3.40it/s, avr_loss=1.23e-4]";

        var ok = SdScriptsLoraTrainer.TryParseProgressLine(line, out var step, out _, out var loss);

        ok.Should().BeTrue();
        step.Should().Be(150);
        loss.Should().NotBeNull().And.BeApproximately(0.000123, 1e-7);
    }

    // ── Test helpers ──────────────────────────────────────────────────────────

    private static LoraTrainingRequest MakeRequest(
        string                            name           = "test_lora",
        string                            baseModelPath  = @"C:\fake\base.safetensors",
        int                               datasetSize    = 20,
        IReadOnlyList<string>?            imageFiles     = null,
        string                            outputDir      = @"C:\temp\loras")
    {
        IReadOnlyList<LoraTrainingImage> dataset;
        if (imageFiles is not null)
            dataset = imageFiles.Select(f => new LoraTrainingImage(f, "test caption")).ToList();
        else
            dataset = Enumerable.Range(0, datasetSize)
                .Select(i => new LoraTrainingImage($@"C:\fake\img{i}.jpg", $"caption {i}"))
                .ToList();

        return new LoraTrainingRequest(
            Name:            name,
            BaseModelPath:   baseModelPath,
            Dataset:         dataset,
            Parameters:      new LoraTrainingParameters(),
            OutputDirectory: outputDir);
    }

    /// <summary>
    /// Vytvoří dočasný „base model" soubor + N dočasných fake obrázků pro
    /// testování validace (která kontroluje File.Exists). Caller je odpovědný
    /// za úklid souborů.
    /// </summary>
    private static (string BaseModel, List<string> Images) CreateTempBaseModel(int datasetSize = 20)
    {
        var baseModel = Path.Combine(Path.GetTempPath(),
            $"loratrainer_basemodel_{Guid.NewGuid():N}.safetensors");
        File.WriteAllText(baseModel, "fake checkpoint header");

        var images = new List<string>();
        for (var i = 0; i < datasetSize; i++)
        {
            var img = Path.Combine(Path.GetTempPath(),
                $"loratrainer_img_{Guid.NewGuid():N}_{i}.jpg");
            File.WriteAllText(img, "fake jpg");
            images.Add(img);
        }

        return (baseModel, images);
    }
}
