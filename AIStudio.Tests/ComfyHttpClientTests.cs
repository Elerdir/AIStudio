using System.Net;
using System.Text;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;
using AIStudio.Infrastructure.Services;
using FluentAssertions;

namespace AIStudio.Tests;

/// <summary>
/// ComfyHttpClient je stateless mapper nad HTTP API ComfyUI serveru.
/// Testujeme přes <see cref="StubHandler"/> — vlastní HttpMessageHandler,
/// který vrací předem nadefinované odpovědi. Žádný skutečný ComfyUI nepotřebujeme.
/// </summary>
public class ComfyHttpClientTests
{
    private const int TestPort = 8188;

    /// <summary>Pomocný HttpMessageHandler — vrací response podle (method+url) lookup.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;
        public StubFactory(StubHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (ComfyHttpClient client, StubHandler handler) MakeClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var client  = new ComfyHttpClient(new StubFactory(handler));
        return (client, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    // ── IsHealthyAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task IsHealthyAsync_200Response_ReturnsTrue()
    {
        var (client, _) = MakeClient(_ => Json("""{"system":"ok"}"""));
        var healthy = await client.IsHealthyAsync(TestPort);
        healthy.Should().BeTrue();
    }

    [Fact]
    public async Task IsHealthyAsync_500Response_ReturnsFalse()
    {
        var (client, _) = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var healthy = await client.IsHealthyAsync(TestPort);
        healthy.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_ConnectionException_ReturnsFalse()
    {
        var (client, _) = MakeClient(_ => throw new HttpRequestException("connection refused"));
        var healthy = await client.IsHealthyAsync(TestPort);
        healthy.Should().BeFalse();
    }

    // ── GetCheckpointsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetCheckpointsAsync_ParsesObjectInfo()
    {
        // Realný formát z ComfyUI /object_info/CheckpointLoaderSimple
        var responseJson = """
        {
            "CheckpointLoaderSimple": {
                "input": {
                    "required": {
                        "ckpt_name": [["sdxl-base.safetensors", "flux1-dev.safetensors"]]
                    }
                }
            }
        }
        """;
        var (client, _) = MakeClient(_ => Json(responseJson));

        var result = await client.GetCheckpointsAsync(TestPort);

        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(new[] { "flux1-dev.safetensors", "sdxl-base.safetensors" });
        // Vrací abecedně seřazeno
    }

    [Fact]
    public async Task GetCheckpointsAsync_EmptyList_ReturnsEmpty()
    {
        var responseJson = """
        { "CheckpointLoaderSimple": { "input": { "required": { "ckpt_name": [[]] } } } }
        """;
        var (client, _) = MakeClient(_ => Json(responseJson));
        var result = await client.GetCheckpointsAsync(TestPort);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCheckpointsAsync_HttpError_ReturnsEmpty()
    {
        var (client, _) = MakeClient(_ => throw new HttpRequestException("fail"));
        var result = await client.GetCheckpointsAsync(TestPort);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLorasAsync_ParsesObjectInfo()
    {
        var responseJson = """
        { "LoraLoader": { "input": { "required": { "lora_name": [["lora1.safetensors", "anime/style.safetensors"]] } } } }
        """;
        var (client, _) = MakeClient(_ => Json(responseJson));

        var result = await client.GetLorasAsync(TestPort);

        result.Should().HaveCount(2);
        result.Should().Contain("lora1.safetensors");
        result.Should().Contain("anime/style.safetensors");
    }

    // ── QueuePromptAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task QueuePromptAsync_Success_ReturnsPromptId()
    {
        var (client, handler) = MakeClient(_ => Json("""{"prompt_id":"abc123","number":42,"node_errors":{}}"""));

        var workflow = new Dictionary<string, object> { ["1"] = "fake-node" };
        var promptId = await client.QueuePromptAsync(TestPort, workflow, "test-client");

        promptId.Should().Be("abc123");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/prompt");
    }

    [Fact]
    public async Task QueuePromptAsync_400WithNodeErrors_ThrowsWithDetails()
    {
        var errorJson = """
        {
            "error": { "message": "Workflow has errors" },
            "node_errors": {
                "4": {
                    "errors": [
                        { "message": "Value not in list", "details": "ckpt_name: 'bad.safetensors' not in []" }
                    ]
                }
            }
        }
        """;
        var (client, _) = MakeClient(_ => Json(errorJson, HttpStatusCode.BadRequest));

        var act = () => client.QueuePromptAsync(TestPort, new Dictionary<string, object>(), "x");

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*Workflow has errors*Value not in list*ckpt_name*");
    }

    [Fact]
    public async Task QueuePromptAsync_400EmptyBody_ThrowsGenericMessage()
    {
        var (client, _) = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(string.Empty)
        });

        var act = () => client.QueuePromptAsync(TestPort, new Dictionary<string, object>(), "x");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── FetchHistoryResultAsync ───────────────────────────────────────────────

    [Fact]
    public async Task FetchHistoryResultAsync_CompletedSuccess_ReturnsImages()
    {
        const string promptId = "abc";
        var historyJson = $$"""
        {
            "{{promptId}}": {
                "status": { "status_str": "success", "completed": true },
                "outputs": {
                    "9": {
                        "images": [
                            { "filename": "ComfyUI_0001.png", "subfolder": "", "type": "output" }
                        ]
                    }
                }
            }
        }
        """;
        var (client, _) = MakeClient(_ => Json(historyJson));

        var result = await client.FetchHistoryResultAsync(TestPort, promptId);

        result.Should().NotBeNull();
        result!.PromptId.Should().Be(promptId);
        result.Images.Should().ContainSingle()
            .Which.Filename.Should().Be("ComfyUI_0001.png");
    }

    [Fact]
    public async Task FetchHistoryResultAsync_HistoryEmpty_ReturnsNull()
    {
        var (client, _) = MakeClient(_ => Json("{}"));
        var result = await client.FetchHistoryResultAsync(TestPort, "abc");
        result.Should().BeNull();
    }

    [Fact]
    public async Task FetchHistoryResultAsync_ErrorStatus_ThrowsComfyExecutionException()
    {
        var historyJson = """
        {
            "abc": {
                "status": {
                    "status_str": "error",
                    "completed": true,
                    "messages": [
                        ["execution_error", { "exception_message": "OutOfMemoryError" }]
                    ]
                },
                "outputs": {}
            }
        }
        """;
        var (client, _) = MakeClient(_ => Json(historyJson));

        var act = () => client.FetchHistoryResultAsync(TestPort, "abc");

        await act.Should().ThrowAsync<ComfyExecutionException>()
                 .WithMessage("*OutOfMemoryError*");
    }

    [Fact]
    public async Task FetchHistoryResultAsync_ErrorWithoutDetail_ThrowsGeneric()
    {
        var historyJson = """
        { "abc": { "status": { "status_str": "error", "completed": true, "messages": [] }, "outputs": {} } }
        """;
        var (client, _) = MakeClient(_ => Json(historyJson));

        var act = () => client.FetchHistoryResultAsync(TestPort, "abc");

        await act.Should().ThrowAsync<ComfyExecutionException>()
                 .WithMessage("*bez detailu*");
    }

    // ── GetQueueDepthAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetQueueDepthAsync_RunningAndPending_ReturnsSum()
    {
        var queueJson = """
        {
            "queue_running": [["job1"], ["job2"]],
            "queue_pending": [["job3"]]
        }
        """;
        var (client, _) = MakeClient(_ => Json(queueJson));
        var depth = await client.GetQueueDepthAsync(TestPort);
        depth.Should().Be(3);
    }

    [Fact]
    public async Task GetQueueDepthAsync_Empty_ReturnsZero()
    {
        var (client, _) = MakeClient(_ => Json("""{"queue_running":[],"queue_pending":[]}"""));
        var depth = await client.GetQueueDepthAsync(TestPort);
        depth.Should().Be(0);
    }

    [Fact]
    public async Task GetQueueDepthAsync_HttpError_ReturnsMinusOne()
    {
        var (client, _) = MakeClient(_ => throw new HttpRequestException("fail"));
        var depth = await client.GetQueueDepthAsync(TestPort);
        depth.Should().Be(-1);
    }

    // ── BuildValidationErrorMessage (přímý unit pro veřejný helper) ───────────

    [Fact]
    public void BuildValidationErrorMessage_ValidNodeErrors_FormatsNicely()
    {
        var json = """
        {
            "error": { "message": "Validation failed" },
            "node_errors": {
                "5": {
                    "errors": [
                        { "message": "Missing input", "details": "vae_name is required" }
                    ]
                }
            }
        }
        """;

        var msg = ComfyHttpClient.BuildValidationErrorMessage(json);
        msg.Should().Contain("Validation failed");
        msg.Should().Contain("Missing input");
        msg.Should().Contain("vae_name is required");
    }

    [Fact]
    public void BuildValidationErrorMessage_NotJson_ReturnsRawString()
    {
        var msg = ComfyHttpClient.BuildValidationErrorMessage("plain text body");
        msg.Should().Contain("plain text body");
    }

    [Fact]
    public void BuildValidationErrorMessage_Empty_ReturnsFallback()
    {
        var msg = ComfyHttpClient.BuildValidationErrorMessage(string.Empty);
        msg.Should().Contain("prázdnou");
    }

    // ── URL composition spot check ────────────────────────────────────────────

    [Fact]
    public async Task DownloadImageAsync_BuildsCorrectQueryString()
    {
        var (client, handler) = MakeClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        });

        await client.DownloadImageAsync(TestPort, "image.png", "subfolder", "output");

        handler.Requests.Should().ContainSingle();
        var uri = handler.Requests[0].RequestUri!.ToString();
        uri.Should().Contain("/view");
        uri.Should().Contain("filename=image.png");
        uri.Should().Contain("subfolder=subfolder");
        uri.Should().Contain("type=output");
    }
}
