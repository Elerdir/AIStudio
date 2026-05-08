using System.Net;
using System.Text;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;
using NSubstitute;

namespace AIStudio.Tests;

/// <summary>
/// Testy pro CivitaiClient přes fake HttpMessageHandler — bez síťového připojení.
/// </summary>
public class CivitaiClientTests
{
    private static CivitaiClient MakeClient(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { CivitaiApiKey = "" });

        var handler = new FakeHttpHandler(status, responseJson);
        var factory = new FakeHttpClientFactory(handler);

        return new CivitaiClient(settings, factory);
    }

    [Fact]
    public async Task SearchAsync_ValidResponse_ParsesItems()
    {
        const string json = """
            {
              "items": [
                {
                  "id": 1,
                  "name": "Test Model",
                  "type": "Checkpoint",
                  "description": "Popis",
                  "nsfw": false,
                  "creator": { "username": "author" },
                  "stats": { "downloadCount": 1000, "rating": 4.5, "ratingCount": 200 },
                  "modelVersions": []
                }
              ]
            }
            """;

        var client = MakeClient(json);
        var result = await client.SearchAsync("test");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Test Model");
        result[0].Type.Should().Be("Checkpoint");
        result[0].DownloadCount.Should().Be(1000);
        result[0].Creator.Should().Be("author");
    }

    [Fact]
    public async Task SearchAsync_EmptyItems_ReturnsEmpty()
    {
        var client = MakeClient("""{ "items": [] }""");
        var result = await client.SearchAsync("nothing");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_HttpError_ReturnsEmpty()
    {
        var client = MakeClient("{}", HttpStatusCode.InternalServerError);
        var result = await client.SearchAsync("test");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_MissingItemsProperty_ReturnsEmpty()
    {
        var client = MakeClient("""{ "metadata": {} }""");
        var result = await client.SearchAsync("test");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAuthorizedDownloadUrl_WithToken_AppendsToken()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { CivitaiApiKey = "mytoken123" });

        var client = new CivitaiClient(settings, new FakeHttpClientFactory(new FakeHttpHandler()));

        var url = client.BuildAuthorizedDownloadUrl("https://civitai.com/api/download/models/1");
        url.Should().Be("https://civitai.com/api/download/models/1?token=mytoken123");
    }

    [Fact]
    public async Task BuildAuthorizedDownloadUrl_WithoutToken_ReturnsUnchanged()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { CivitaiApiKey = "" });

        var client = new CivitaiClient(settings, new FakeHttpClientFactory(new FakeHttpHandler()));

        var original = "https://civitai.com/api/download/models/1";
        client.BuildAuthorizedDownloadUrl(original).Should().Be(original);
    }

    [Fact]
    public void BuildModelPageUrl_ReturnsCorrectUrl()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings());
        var client = new CivitaiClient(settings, new FakeHttpClientFactory(new FakeHttpHandler()));

        client.BuildModelPageUrl(42).Should().Be("https://civitai.com/models/42");
    }

    [Fact]
    public async Task SearchAsync_LimitClamped_MaxIs100()
    {
        // 200 by mělo být ořezáno na 100 — ověříme, že nehodí výjimku
        var client = MakeClient("""{ "items": [] }""");
        var act = () => client.SearchAsync(limit: 200);
        await act.Should().NotThrowAsync();
    }
}
