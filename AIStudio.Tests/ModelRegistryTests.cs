using AIStudio.Core.Models;
using FluentAssertions;

namespace AIStudio.Tests;

public class ModelRegistryTests
{
    [Theory]
    [InlineData("Llama 3.1 8B Instruct Q4_K_M",    "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf")]
    [InlineData("Phi-4 Q4_K_M",                      "phi-4-Q4_K_M.gguf")]
    [InlineData("Qwen3 8B Q4_K_M",                   "Qwen3-8B-Q4_K_M.gguf")]
    [InlineData("Magnum v4 22B Q4_K_M",              "magnum-v4-22b-Q4_K_M.gguf")]
    public void GetFileName_KnownModel_ReturnsCorrectFilename(string displayName, string expected)
    {
        ModelRegistry.GetFileName(displayName).Should().Be(expected);
    }

    [Fact]
    public void GetFileName_UnknownModel_ReturnsNull()
    {
        ModelRegistry.GetFileName("Neexistující model XYZ").Should().BeNull();
    }

    [Theory]
    [InlineData("llama 3.1 8b instruct q4_k_m")]
    [InlineData("LLAMA 3.1 8B INSTRUCT Q4_K_M")]
    [InlineData("Llama 3.1 8B Instruct Q4_K_M")]
    public void GetFileName_CaseInsensitive_Works(string input)
    {
        ModelRegistry.GetFileName(input).Should().Be("Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf");
    }

    [Fact]
    public void ChatModels_ContainsOnlyChatKind()
    {
        ModelRegistry.ChatModels.Should().AllSatisfy(m => m.Kind.Should().Be(ModelKind.Chat));
    }

    [Fact]
    public void ChatModels_NotEmpty()
    {
        ModelRegistry.ChatModels.Should().NotBeEmpty();
    }

    [Fact]
    public void AsFileNameDictionary_HasAllChatModels()
    {
        var dict = ModelRegistry.AsFileNameDictionary();
        dict.Should().NotBeEmpty();
        foreach (var model in ModelRegistry.ChatModels)
            dict.Should().ContainKey(model.DisplayName);
    }

    [Fact]
    public void AsFileNameDictionary_IsCaseInsensitive()
    {
        var dict = ModelRegistry.AsFileNameDictionary();
        dict.Should().ContainKey("phi-4 q4_k_m");
    }

    [Fact]
    public void ModelDefinition_DisplayNameAndFileName_AreNotEmpty()
    {
        foreach (var model in ModelRegistry.ChatModels)
        {
            model.DisplayName.Should().NotBeNullOrWhiteSpace();
            model.FileName.Should().NotBeNullOrWhiteSpace();
            model.FileName.Should().EndWith(".gguf");
        }
    }
}
