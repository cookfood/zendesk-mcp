using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class ZendeskClientTests
{
    private static (ZendeskClient Client, CapturingHandler Handler) CreateClient()
    {
        var handler = new CapturingHandler();
        var factory = new StubHttpClientFactory(handler);
        var options = Options.Create(new ZendeskOptions
        {
            Subdomain = "acme",
            Email = "agent@acme.com",
            ApiToken = "secret-token",
        });
        return (new ZendeskClient(factory, options), handler);
    }

    [Fact]
    public async Task SendAsync_BuildsBaseUrl_AndBasicAuthHeader()
    {
        var (client, handler) = CreateClient();

        await client.SendAsync(HttpMethod.Get, "tickets/123.json");

        Assert.Equal("https://acme.zendesk.com/api/v2/tickets/123.json", handler.LastRequest!.RequestUri!.ToString());

        var auth = handler.LastRequest.Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("agent@acme.com/token:secret-token"));
        Assert.Equal(expected, auth.Parameter);
    }

    [Fact]
    public async Task SendAsync_AppendsQueryString()
    {
        var (client, handler) = CreateClient();

        await client.SendAsync(HttpMethod.Get, "tickets.json", new Dictionary<string, string?>
        {
            ["page"] = "2",
            ["per_page"] = "50",
            ["empty"] = "",
        });

        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("page=2", url);
        Assert.Contains("per_page=50", url);
        Assert.DoesNotContain("empty=", url);
    }

    [Fact]
    public async Task SendAsync_Throws_OnErrorStatus()
    {
        var (client, handler) = CreateClient();
        handler.StatusCode = HttpStatusCode.NotFound;
        handler.Body = "{\"error\":\"RecordNotFound\"}";

        var ex = await Assert.ThrowsAsync<ZendeskApiException>(() => client.SendAsync(HttpMethod.Get, "tickets/999.json"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("RecordNotFound", ex.Message);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
