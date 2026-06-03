using AIStudio.Core.Interfaces;
using AIStudio.Core.Services;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

public class WanModelsTests
{
    [Fact]
    public void RequiredFiles_TextToVideo_ExcludesClipVision()
    {
        var files = WanModels.RequiredFiles(WanModels.T2V_14B);
        var names = files.Select(f => f.FileName).ToList();

        names.Should().Contain(WanModels.TextEncoder.FileName)
             .And.Contain(WanModels.Vae.FileName)
             .And.Contain(WanModels.T2V_14B.DiffusionModel.FileName)
             .And.NotContain(WanModels.ClipVision.FileName);
    }

    [Fact]
    public void RequiredFiles_ImageToVideo_IncludesClipVision()
    {
        var files = WanModels.RequiredFiles(WanModels.I2V_480P_14B).Select(f => f.FileName).ToList();
        files.Should().Contain(WanModels.ClipVision.FileName)
             .And.Contain(WanModels.I2V_480P_14B.DiffusionModel.FileName);
    }

    [Fact]
    public void Catalog_AllUrls_AreWellFormedFromRepoBase()
    {
        foreach (var f in new[] { WanModels.TextEncoder, WanModels.Vae, WanModels.ClipVision,
                                  WanModels.T2V_14B.DiffusionModel, WanModels.I2V_480P_14B.DiffusionModel })
        {
            f.Url.Should().StartWith(WanModels.RepoBase);
            f.Url.Should().EndWith(f.FileName);
            f.Url.Should().Contain("/" + f.Subdir + "/");
        }
    }

    [Fact]
    public void FindById_ReturnsModelOrNull()
    {
        WanModels.FindById("wan21-t2v-14b").Should().Be(WanModels.T2V_14B);
        WanModels.FindById("nope").Should().BeNull();
    }

    [Fact]
    public void DiffusionFileNames_MatchWorkflowBuilderExpectations()
    {
        // Buildery adresují modely pouhým názvem — katalogové názvy musí sedět
        // s tím, co umí ComfyUI loadery (UNETLoader/CLIPLoader/VAELoader).
        WanModels.TextEncoder.FileName.Should().Be(ComfyWorkflowBuilder.DefaultWanTextEncoder);
        WanModels.Vae.FileName.Should().Be(ComfyWorkflowBuilder.DefaultWanVae);
        WanModels.ClipVision.FileName.Should().Be(ComfyWorkflowBuilder.DefaultWanClipVision);
    }

    // ── WanDependencyService.FindMissing (s temp adresářem) ────────────────────

    [Fact]
    public void FindMissing_EmptyDir_ReturnsAllLabels()
    {
        var svc = new WanDependencyService(Substitute.For<IDownloadService>());
        var dir = Path.Combine(Path.GetTempPath(), "wandep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var missing = svc.FindMissing(dir, WanModels.T2V_1_3B);
            missing.Should().HaveCount(WanModels.RequiredFiles(WanModels.T2V_1_3B).Count);
            svc.AreDependenciesPresent(dir, WanModels.T2V_1_3B).Should().BeFalse();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FindMissing_AllFilesPresentInSubdirs_ReturnsEmpty()
    {
        var svc = new WanDependencyService(Substitute.For<IDownloadService>());
        var dir = Path.Combine(Path.GetTempPath(), "wandep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in WanModels.RequiredFiles(WanModels.T2V_1_3B))
            {
                var sub = Path.Combine(dir, f.Subdir);
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, f.FileName), "x");
            }

            svc.FindMissing(dir, WanModels.T2V_1_3B).Should().BeEmpty();
            svc.AreDependenciesPresent(dir, WanModels.T2V_1_3B).Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
