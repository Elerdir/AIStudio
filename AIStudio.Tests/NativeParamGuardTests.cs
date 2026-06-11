using AIStudio.Core.Models;
using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class NativeParamGuardTests
{
    [Fact]
    public void Correct_SdModelWithFluxParams_IsFixed()
    {
        // SD1.5 model omylem s FLUX defaulty (CFG 1.0, 4 kroky) → oprava na rodinné defaulty.
        var (steps, cfg, corrected) = NativeParamGuard.Correct(
            NativeModelFamily.Sd1, "realisticVision_v5.safetensors", steps: 4, cfg: 1.0);

        corrected.Should().BeTrue();
        cfg.Should().Be(7.0);     // SD1 default
        steps.Should().Be(20);
    }

    [Fact]
    public void Correct_Sdxl_FixesCfgToSdxlDefault()
    {
        var (steps, cfg, corrected) = NativeParamGuard.Correct(
            NativeModelFamily.Sdxl, "juggernautXL.safetensors", steps: 4, cfg: 1.0);

        corrected.Should().BeTrue();
        cfg.Should().Be(6.0);     // SDXL default
        steps.Should().Be(25);
    }

    [Fact]
    public void Correct_Flux_LeftUntouched()
    {
        var (steps, cfg, corrected) = NativeParamGuard.Correct(
            NativeModelFamily.Flux, "flux1-schnell.safetensors", steps: 4, cfg: 1.0);

        corrected.Should().BeFalse();
        cfg.Should().Be(1.0);
        steps.Should().Be(4);
    }

    [Fact]
    public void Correct_FastVariant_LeftUntouched()
    {
        // SDXL Turbo / Lightning běží na nízké CFG záměrně.
        foreach (var name in new[] { "sdxl_turbo.safetensors", "dreamshaperXL_lightning.safetensors",
                                     "model_lcm.safetensors", "hyperSD.safetensors" })
        {
            var (steps, cfg, corrected) = NativeParamGuard.Correct(
                NativeModelFamily.Sdxl, name, steps: 6, cfg: 1.0);
            corrected.Should().BeFalse($"protože {name} je rychlá varianta");
            cfg.Should().Be(1.0);
        }
    }

    [Fact]
    public void Correct_ReasonableCfg_LeftUntouched()
    {
        // Uživatel nastavil reálnou guidance → nesahat ani na SD.
        var (steps, cfg, corrected) = NativeParamGuard.Correct(
            NativeModelFamily.Sd1, "model.safetensors", steps: 25, cfg: 7.0);

        corrected.Should().BeFalse();
        cfg.Should().Be(7.0);
        steps.Should().Be(25);
    }

    [Fact]
    public void Correct_OnlyStepsLow_KeepsUserSteps_WhenCfgFixed()
    {
        // CFG je flux-ish (oprava), ale kroky má uživatel rozumné → kroky zachovat.
        var (steps, cfg, corrected) = NativeParamGuard.Correct(
            NativeModelFamily.Sd1, "model.safetensors", steps: 30, cfg: 1.0);

        corrected.Should().BeTrue();
        cfg.Should().Be(7.0);
        steps.Should().Be(30);    // > FluxishStepThreshold → neměníme
    }

    [Fact]
    public void Correct_UnknownFamily_FixesWithSafeSdDefaults()
    {
        // Bez markeru v názvu (Unknown) + CFG 1 → bezpečné SD1.5 defaulty.
        var (steps, cfg, corrected) = NativeParamGuard.Correct(
            NativeModelFamily.Unknown, "myCoolModel.safetensors", steps: 4, cfg: 1.0);

        corrected.Should().BeTrue();
        cfg.Should().Be(7.0);
        steps.Should().Be(20);
    }
}
