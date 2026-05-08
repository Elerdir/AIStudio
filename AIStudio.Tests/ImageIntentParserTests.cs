using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

public class ImageIntentParserTests
{
    private ILlamaService MakeLlama(bool isLoaded = true, string? jsonResponse = null)
    {
        var llama = Substitute.For<ILlamaService>();
        llama.IsLoaded.Returns(isLoaded);

        var tokens = jsonResponse is null
            ? Array.Empty<string>()
            : new[] { jsonResponse };

        llama.ChatAsync(
                Arg.Any<IReadOnlyList<(string, string)>>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(tokens.ToAsyncEnumerable());

        return llama;
    }

    [Fact]
    public async Task ParseAsync_EmptyPrompt_ReturnsFallbackWithAutoKind()
    {
        var parser = new ImageIntentParser(MakeLlama());
        var result = await parser.ParseAsync("");

        result.Kind.Should().Be(ImageKind.Auto);
        result.EnglishPrompt.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_WhitespacePrompt_ReturnsFallback()
    {
        var parser = new ImageIntentParser(MakeLlama());
        var result = await parser.ParseAsync("   ");

        result.Kind.Should().Be(ImageKind.Auto);
    }

    [Fact]
    public async Task ParseAsync_LlamaNotLoaded_ReturnsFallbackWithOriginalPrompt()
    {
        var parser = new ImageIntentParser(MakeLlama(isLoaded: false));
        var result = await parser.ParseAsync("kočka na střeše");

        result.Kind.Should().Be(ImageKind.Auto);
        result.EnglishPrompt.Should().Be("kočka na střeše");
        result.Reasoning.Should().Contain("Fallback");
    }

    [Fact]
    public async Task ParseAsync_ValidJson_ParsesKindAspectQuality()
    {
        var json = """
            {
              "kind": "anime",
              "aspect": "landscape",
              "quality": "hi-res",
              "english_prompt": "cat on a rooftop, anime style",
              "negative_prompt": "realistic, photo",
              "reasoning": "anime keywords detected"
            }
            """;

        var parser = new ImageIntentParser(MakeLlama(jsonResponse: json));
        var result = await parser.ParseAsync("anime kočka na střeše");

        result.Kind.Should().Be(ImageKind.Anime);
        result.Aspect.Should().Be(ImageAspect.Landscape);
        result.Quality.Should().Be(ImageQualityHint.HighRes);
        result.EnglishPrompt.Should().Be("cat on a rooftop, anime style");
        result.NegativePrompt.Should().Be("realistic, photo");
    }

    [Fact]
    public async Task ParseAsync_RealisticJson_ParsesRealistic()
    {
        var json = """
            {
              "kind": "realistic",
              "aspect": "portrait",
              "quality": "normal",
              "english_prompt": "portrait of a woman",
              "negative_prompt": "blurry",
              "reasoning": "realistic photo"
            }
            """;

        var parser = new ImageIntentParser(MakeLlama(jsonResponse: json));
        var result = await parser.ParseAsync("fotka ženy");

        result.Kind.Should().Be(ImageKind.Realistic);
        result.Aspect.Should().Be(ImageAspect.Portrait);
        result.Quality.Should().Be(ImageQualityHint.Normal);
    }

    [Fact]
    public async Task ParseAsync_InvalidJson_ReturnsFallback()
    {
        var parser = new ImageIntentParser(MakeLlama(jsonResponse: "tohle není JSON"));
        var result = await parser.ParseAsync("test prompt");

        result.Kind.Should().Be(ImageKind.Auto);
        result.Reasoning.Should().Contain("Fallback");
    }

    [Fact]
    public async Task ParseAsync_JsonWithBrokenShape_ReturnsFallback()
    {
        var json = """{ "kind": 42, "aspect": null }""";
        var parser = new ImageIntentParser(MakeLlama(jsonResponse: json));
        var result = await parser.ParseAsync("test");

        result.Should().NotBeNull();
        result.Kind.Should().Be(ImageKind.Auto);
    }

    [Fact]
    public async Task ParseAsync_JsonWrappedInMarkdown_ExtractsJson()
    {
        var response = """
            Sure, here is your JSON:
            {
              "kind": "stylized",
              "aspect": "square",
              "quality": "fast",
              "english_prompt": "digital art",
              "negative_prompt": "photo",
              "reasoning": "stylized"
            }
            Let me know if you need changes.
            """;

        var parser = new ImageIntentParser(MakeLlama(jsonResponse: response));
        var result = await parser.ParseAsync("digitální art");

        result.Kind.Should().Be(ImageKind.Stylized);
        result.Quality.Should().Be(ImageQualityHint.Fast);
    }
}

internal static class AsyncEnumerableExtensions
{
    internal static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
            yield return item;
        await Task.CompletedTask;
    }
}
