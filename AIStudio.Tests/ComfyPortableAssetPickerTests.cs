using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Regresní testy výběru NVIDIA portable assetu. Původní logika preferovala nejvyšší
/// cuNNN sufix a bezsufixový build (nejnovější CUDA, jediný s podporou RTX 50/Blackwell)
/// skórovala nulou — na moderních kartách tak vybírala legacy cu126 build.
/// </summary>
public class ComfyPortableAssetPickerTests
{
    /// <summary>Skutečné názvy assetů z ComfyUI v0.25.1 release.</summary>
    private static readonly string[] Release0251 =
    {
        "ComfyUI_windows_portable_amd.7z",
        "ComfyUI_windows_portable_intel.7z",
        "ComfyUI_windows_portable_nvidia.7z",
        "ComfyUI_windows_portable_nvidia_cu126.7z",
    };

    [Fact]
    public void PickBest_ModernGpu_PrefersPlainNvidiaBuild()
    {
        // RTX 20+ (vč. RTX 50) → bezsufixový build s nejnovější CUDA.
        ComfyPortableAssetPicker.PickBest(Release0251, legacyGpu: false)
            .Should().Be("ComfyUI_windows_portable_nvidia.7z");
    }

    [Fact]
    public void PickBest_LegacyGpu_PrefersCu126()
    {
        ComfyPortableAssetPicker.PickBest(Release0251, legacyGpu: true)
            .Should().Be("ComfyUI_windows_portable_nvidia_cu126.7z");
    }

    [Fact]
    public void PickBest_OnlyCuVariants_ModernTakesHighestCuda()
    {
        // Starší styl release bez bezsufixové varianty.
        var assets = new[]
        {
            "ComfyUI_windows_portable_nvidia_cu126.7z",
            "ComfyUI_windows_portable_nvidia_cu128.7z",
        };
        ComfyPortableAssetPicker.PickBest(assets, legacyGpu: false)
            .Should().Be("ComfyUI_windows_portable_nvidia_cu128.7z");
        ComfyPortableAssetPicker.PickBest(assets, legacyGpu: true)
            .Should().Be("ComfyUI_windows_portable_nvidia_cu126.7z");
    }

    [Fact]
    public void PickBest_IgnoresAmdIntelAndNonArchives()
    {
        var assets = new[]
        {
            "ComfyUI_windows_portable_amd.7z",
            "ComfyUI_windows_portable_intel.7z",
            "ComfyUI_windows_portable_nvidia.zip",   // špatná přípona
            "source-code.tar.gz",
        };
        ComfyPortableAssetPicker.PickBest(assets, legacyGpu: false).Should().BeNull();
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", false)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", false)]
    [InlineData("NVIDIA GeForce RTX 3090", false)]
    [InlineData("NVIDIA GeForce RTX 2060", false)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", true)]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", true)]
    [InlineData("NVIDIA GeForce GTX 970", true)]
    [InlineData("NVIDIA GeForce GTX 760", true)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLegacyNvidiaGpu_ClassifiesGenerations(string? name, bool expected)
    {
        ComfyPortableAssetPicker.IsLegacyNvidiaGpu(name).Should().Be(expected);
    }
}
