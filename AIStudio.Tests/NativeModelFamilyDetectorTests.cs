using AIStudio.Core.Models;
using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class NativeModelFamilyDetectorTests
{
    [Theory]
    [InlineData("flux1-schnell-q4.gguf", NativeModelFamily.Flux)]
    [InlineData("FLUX.1-dev.safetensors", NativeModelFamily.Flux)]
    [InlineData("sd3_medium.safetensors", NativeModelFamily.Sd3)]
    [InlineData("sd_xl_base_1.0.safetensors", NativeModelFamily.Sdxl)]
    [InlineData("juggernautXL_v9.safetensors", NativeModelFamily.Sdxl)]
    [InlineData("ponyDiffusionV6.safetensors", NativeModelFamily.Sdxl)]
    [InlineData("v2-1_768-ema.safetensors", NativeModelFamily.Sd2)]
    [InlineData("v1-5-pruned-emaonly.safetensors", NativeModelFamily.Sd1)]
    [InlineData("dreamshaper_8.safetensors", NativeModelFamily.Unknown)]
    public void GuessFromFileName_Classifies(string name, NativeModelFamily expected)
    {
        NativeModelFamilyDetector.GuessFromFileName(name).Should().Be(expected);
    }

    [Fact]
    public void GuessFromFileName_UsesFileNameNotPath()
    {
        NativeModelFamilyDetector.GuessFromFileName(@"C:\flux\sd_xl_base.safetensors")
            .Should().Be(NativeModelFamily.Sdxl);   // rozhoduje název souboru, ne složka
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GuessFromFileName_BlankIsUnknown(string? input)
    {
        NativeModelFamilyDetector.GuessFromFileName(input).Should().Be(NativeModelFamily.Unknown);
    }

    [Fact]
    public void FluxTakesPrecedenceOverXl()
    {
        // „flux" má přednost i kdyby název obsahoval i „xl"-ish substring
        NativeModelFamilyDetector.GuessFromFileName("flux-xl-merge.gguf").Should().Be(NativeModelFamily.Flux);
    }
}
