using AIStudio.Core.Services;
using FluentAssertions;

namespace AIStudio.Tests;

public class ModelFoldersTests
{
    [Theory]
    [InlineData("LORA",   "loras")]
    [InlineData("lora",   "loras")]
    [InlineData("LoCon",  "loras")]
    [InlineData("LyCORIS","loras")]
    [InlineData("VAE",    "vae")]
    [InlineData("Controlnet", "controlnet")]
    [InlineData("TextualInversion", "embeddings")]
    [InlineData("Upscaler", "upscale_models")]
    public void ResolveSubfolder_KnownTypes_MapToFolders(string type, string expected) =>
        ModelFolders.ResolveSubfolder(type).Should().Be(expected);

    [Theory]
    [InlineData("Checkpoint")]
    [InlineData("Hypernetwork")]
    [InlineData("Poses")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolveSubfolder_RootTypes_ReturnEmpty(string? type) =>
        ModelFolders.ResolveSubfolder(type).Should().BeEmpty();

    [Fact]
    public void ResolveSubfolder_UnknownType_FallsBackToFilenameHeuristic()
    {
        // typ chybí, ale název napovídá LoRA
        ModelFolders.ResolveSubfolder(null, "my_style_lora.safetensors").Should().Be("loras");
        ModelFolders.ResolveSubfolder("", "character_lycoris.safetensors").Should().Be("loras");
        // běžný checkpoint název → root
        ModelFolders.ResolveSubfolder("", "juggernaut_xl.safetensors").Should().BeEmpty();
    }
}
