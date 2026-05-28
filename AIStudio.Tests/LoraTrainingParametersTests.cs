using AIStudio.Core.Models;
using FluentAssertions;

namespace AIStudio.Tests;

public class LoraTrainingParametersTests
{
    // ── DefaultsFor: detekce typu modelu podle filename ───────────────────────

    [Theory]
    [InlineData("sd_xl_base_1.0.safetensors")]
    [InlineData("JuggernautXL_v9.safetensors")]
    [InlineData("Animagine-XL-3.1.safetensors")]
    [InlineData("realvisxl_v5.safetensors")]
    public void DefaultsFor_SdxlModel_Uses1024Resolution(string filename)
    {
        var p = LoraTrainingParameters.DefaultsFor(filename);
        p.Resolution.Should().Be(1024, "SDXL nativní rozlišení");
        p.Rank.Should().Be(32);
        p.Alpha.Should().Be(16);
    }

    [Theory]
    [InlineData("flux1-schnell-Q4_K_M.gguf")]
    [InlineData("FLUX.1-dev-fp8.safetensors")]
    public void DefaultsFor_FluxModel_UsesLowerRank(string filename)
    {
        var p = LoraTrainingParameters.DefaultsFor(filename);
        p.Rank.Should().Be(16, "FLUX je citlivý na rank, 16 je sweet spot");
        p.Steps.Should().Be(2000, "FLUX vyžaduje víc stepů pro konvergenci");
        p.Resolution.Should().Be(1024);
    }

    [Theory]
    [InlineData("dreamshaper_8_pruned.safetensors")]
    [InlineData("realistic_vision_v6.safetensors")]
    [InlineData("absolutereality_v1.safetensors")]
    public void DefaultsFor_Sd15Model_Uses512Resolution(string filename)
    {
        var p = LoraTrainingParameters.DefaultsFor(filename);
        p.Resolution.Should().Be(512, "SD 1.5 nativní rozlišení");
        p.Rank.Should().Be(32);
    }

    [Fact]
    public void DefaultsFor_DefaultParameters_AreReasonable()
    {
        var p = new LoraTrainingParameters();
        p.Rank.Should().BeInRange(8, 64, "rank by měl být v rozumném rozmezí");
        p.Steps.Should().BeInRange(500, 3000);
        p.LearningRate.Should().BeInRange(1e-5, 1e-3);
        p.BatchSize.Should().BeInRange(1, 8);
        p.MixedPrecisionFp16.Should().BeTrue();
        p.GradientCheckpointing.Should().BeTrue();
        p.Optimizer.Should().Be("AdamW8bit");
    }
}
