using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// Testy pro <see cref="RecommendedModels.PickForGpu"/> — heuristika výběru
/// chat + image modelu podle detekované GPU. Edge cases: žádná GPU, malá VRAM,
/// AMD karta i s vysokou VRAM (kvůli DirectML pomalosti), Apple Silicon.
/// </summary>
public class RecommendedModelsTests
{
    [Fact]
    public void All_ContainsBothTiers()
    {
        // Sanity check — All by mělo obsahovat všechny modely z obou tierů
        var allIds = RecommendedModels.All.Select(m => m.Id).ToHashSet();
        RecommendedModels.DefaultTier.Should().OnlyContain(m => allIds.Contains(m.Id));
        RecommendedModels.LowTier.Should().OnlyContain(m => allIds.Contains(m.Id));
    }

    [Fact]
    public void DefaultTier_HasOneChatAndOneImage()
    {
        RecommendedModels.DefaultTier.Should().HaveCount(2);
        RecommendedModels.DefaultTier.Count(m => m.Kind == RecommendedModelKind.Chat).Should().Be(1);
        RecommendedModels.DefaultTier.Count(m => m.Kind == RecommendedModelKind.Image).Should().Be(1);
    }

    [Fact]
    public void LowTier_HasOneChatAndOneImage()
    {
        RecommendedModels.LowTier.Should().HaveCount(2);
        RecommendedModels.LowTier.Count(m => m.Kind == RecommendedModelKind.Chat).Should().Be(1);
        RecommendedModels.LowTier.Count(m => m.Kind == RecommendedModelKind.Image).Should().Be(1);
    }

    [Fact]
    public void LowTier_FitsInto4GbVram()
    {
        // Low tier musí vejít do 4 GB VRAM uživatele s integrovanou nebo entry GPU.
        // Suma chat + image by neměla překročit ~5 GB stažení.
        var totalGb = RecommendedModels.LowTier.Sum(m => m.SizeBytes) / 1_073_741_824.0;
        totalGb.Should().BeLessOrEqualTo(6.0, "low tier má cílit na 4 GB VRAM stroje");
    }

    // ── PickForGpu — vendor + VRAM logika ────────────────────────────────────

    [Fact]
    public void PickForGpu_Null_ReturnsLowTier()
    {
        // Bez detekce GPU jsme konzervativní — low tier funguje všude
        RecommendedModels.PickForGpu(null).Should().BeEquivalentTo(RecommendedModels.LowTier);
    }

    [Fact]
    public void PickForGpu_NvidiaHighVram_ReturnsDefaultTier()
    {
        var rtx4070 = new Gpu(GpuVendor.Nvidia, "RTX 4070", 12L * 1024 * 1024 * 1024, GpuBackend.Cuda);
        RecommendedModels.PickForGpu(rtx4070).Should().BeEquivalentTo(RecommendedModels.DefaultTier);
    }

    [Fact]
    public void PickForGpu_NvidiaLowVram_ReturnsLowTier()
    {
        // Entry NVIDIA s 4 GB VRAM by se 8B modelem dusila — raději low tier
        var entry = new Gpu(GpuVendor.Nvidia, "GTX 1650", 4L * 1024 * 1024 * 1024, GpuBackend.Cuda);
        RecommendedModels.PickForGpu(entry).Should().BeEquivalentTo(RecommendedModels.LowTier);
    }

    [Fact]
    public void PickForGpu_NvidiaExactly8Gb_ReturnsDefaultTier()
    {
        // Hraniční případ — RTX 3060 s 8 GB VRAM. Default tier by se měl ještě vejít.
        var rtx3060 = new Gpu(GpuVendor.Nvidia, "RTX 3060", 8L * 1024 * 1024 * 1024, GpuBackend.Cuda);
        RecommendedModels.PickForGpu(rtx3060).Should().BeEquivalentTo(RecommendedModels.DefaultTier);
    }

    [Fact]
    public void PickForGpu_AmdHighVram_StillLowTier()
    {
        // AMD RX 6750 má 12 GB VRAM, ale generování FLUX přes DirectML je 1+ min.
        // První dojem uživatele zachráníme low tierem (SD 1.5 = pár vteřin na DirectML).
        var rx6750 = new Gpu(GpuVendor.Amd, "Radeon RX 6750", 12L * 1024 * 1024 * 1024, GpuBackend.Vulkan);
        RecommendedModels.PickForGpu(rx6750).Should().BeEquivalentTo(RecommendedModels.LowTier);
    }

    [Fact]
    public void PickForGpu_IntelArc_ReturnsLowTier()
    {
        var arc = new Gpu(GpuVendor.Intel, "Arc A770", 16L * 1024 * 1024 * 1024, GpuBackend.Vulkan);
        RecommendedModels.PickForGpu(arc).Should().BeEquivalentTo(RecommendedModels.LowTier);
    }

    [Fact]
    public void PickForGpu_AppleSilicon_ReturnsDefaultTier()
    {
        // Apple M-čip má unified memory + Metal velmi efektivně utilizuje
        // dostupnou RAM jako VRAM. M1/M2 s 16+ GB uveze 8B model bez potíží.
        var m2 = new Gpu(GpuVendor.Apple, "Apple M2 Pro", 16L * 1024 * 1024 * 1024, GpuBackend.Metal);
        RecommendedModels.PickForGpu(m2).Should().BeEquivalentTo(RecommendedModels.DefaultTier);
    }

    [Fact]
    public void PickForGpu_Unknown_ReturnsLowTier()
    {
        var unknown = new Gpu(GpuVendor.Unknown, "Žádná GPU", 0, GpuBackend.Cpu);
        RecommendedModels.PickForGpu(unknown).Should().BeEquivalentTo(RecommendedModels.LowTier);
    }

    // ── FindById ─────────────────────────────────────────────────────────────

    [Fact]
    public void FindById_ExistingId_ReturnsModel()
    {
        RecommendedModels.FindById("llama-3.1-8b-instruct-q4km")
            .Should().NotBeNull()
            .And.Subject.As<RecommendedModel>().Kind.Should().Be(RecommendedModelKind.Chat);
    }

    [Fact]
    public void FindById_LowTierIds_AlsoFound()
    {
        RecommendedModels.FindById("llama-3.2-3b-instruct-q4km").Should().NotBeNull();
        RecommendedModels.FindById("dreamshaper-8-sd15").Should().NotBeNull();
    }

    [Fact]
    public void FindById_Unknown_ReturnsNull()
    {
        RecommendedModels.FindById("does-not-exist").Should().BeNull();
    }
}
