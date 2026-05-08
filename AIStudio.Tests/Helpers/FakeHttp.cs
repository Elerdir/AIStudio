using System.Net;
using System.Text;

namespace AIStudio.Tests;

/// <summary>
/// Jednoduchý fake HttpMessageHandler pro testování HTTP klientů bez sítě.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string         _responseBody;

    public FakeHttpHandler(HttpStatusCode status = HttpStatusCode.OK, string responseBody = "")
    {
        _status       = status;
        _responseBody = responseBody;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

/// <summary>
/// Fake IHttpClientFactory vracející HttpClient s daným handlerem.
/// </summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
