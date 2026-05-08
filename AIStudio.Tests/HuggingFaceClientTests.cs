using System.Net;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

public class HuggingFaceClientTests
{
    private static HuggingFaceClient MakeClient(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { HuggingFaceToken = "" });

        var handler = new FakeHttpHandler(status, responseJson);
        var factory = new FakeHttpClientFactory(handler);

        return new HuggingFaceClient(settings, factory);
    }

    // ── SearchModelsAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchModelsAsync_ValidResponse_ParsesModels()
    {
        const string json = """
            [
              { "id": "TheBloke/Llama-2-7B-GGUF", "downloads": 50000, "likes": 300, "lastModified": "2024-01-15T10:00:00Z" },
              { "id": "microsoft/phi-4-gguf",       "downloads": 20000, "likes": 150, "lastModified": "2024-02-01T08:00:00Z" }
            ]
            """;

        var client = MakeClient(json);
        var result = await client.SearchModelsAsync(query: "llama");

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("TheBloke/Llama-2-7B-GGUF");
        result[0].Downloads.Should().Be(50000);
        result[0].Likes.Should().Be(300);
    }

    [Fact]
    public async Task SearchModelsAsync_EmptyArray_ReturnsEmpty()
    {
        var client = MakeClient("[]");
        var result = await client.SearchModelsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchModelsAsync_HttpError_ReturnsEmpty()
    {
        var client = MakeClient("{}", HttpStatusCode.Unauthorized);
        var result = await client.SearchModelsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchModelsAsync_ItemsWithEmptyId_Skipped()
    {
        const string json = """
            [
              { "id": "", "downloads": 100, "likes": 5 },
              { "id": "valid/model", "downloads": 200, "likes": 10 }
            ]
            """;
        var client = MakeClient(json);
        var result = await client.SearchModelsAsync();
        result.Should().HaveCount(1);
        result[0].Id.Should().Be("valid/model");
    }

    // ── GetModelDescriptionAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetModelDescriptionAsync_WithCardDataSummary_ReturnsSummary()
    {
        const string json = """
            {
              "cardData": { "summary": "Výborný model pro generování textu." },
              "tags": ["text-generation", "czech"]
            }
            """;

        var client = MakeClient(json);
        var desc = await client.GetModelDescriptionAsync("some/model");

        desc.Should().Be("Výborný model pro generování textu.");
    }

    [Fact]
    public async Task GetModelDescriptionAsync_NoSummary_FallsBackToTags()
    {
        const string json = """
            {
              "tags": ["text-generation", "gguf", "llama"]
            }
            """;

        var client = MakeClient(json);
        var desc = await client.GetModelDescriptionAsync("some/model");

        desc.Should().Contain("text-generation");
        desc.Should().Contain("gguf");
    }

    [Fact]
    public async Task GetModelDescriptionAsync_HttpError_ReturnsEmpty()
    {
        var client = MakeClient("{}", HttpStatusCode.NotFound);
        var desc = await client.GetModelDescriptionAsync("nonexistent/model");
        desc.Should().BeEmpty();
    }

    [Fact]
    public async Task GetModelDescriptionAsync_EmptyRepoId_ReturnsEmpty()
    {
        var client = MakeClient("{}");
        var desc = await client.GetModelDescriptionAsync("");
        desc.Should().BeEmpty();
    }

    // ── BuildDownloadUrl / BuildModelPageUrl ─────────────────────────────────

    [Fact]
    public void BuildDownloadUrl_ReturnsCorrectFormat()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings());
        var client = new HuggingFaceClient(settings, new FakeHttpClientFactory(new FakeHttpHandler()));

        var url = client.BuildDownloadUrl("TheBloke/Llama-2-7B-GGUF", "llama-2-7b.Q4_K_M.gguf");
        url.Should().Be("https://huggingface.co/TheBloke/Llama-2-7B-GGUF/resolve/main/llama-2-7b.Q4_K_M.gguf");
    }

    [Fact]
    public void BuildModelPageUrl_ReturnsCorrectFormat()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings());
        var client = new HuggingFaceClient(settings, new FakeHttpClientFactory(new FakeHttpHandler()));

        client.BuildModelPageUrl("TheBloke/Llama-2-7B-GGUF")
              .Should().Be("https://huggingface.co/TheBloke/Llama-2-7B-GGUF");
    }
}
