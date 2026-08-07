using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ZendeskMcp.Server.Tools;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class AttachmentToolsTests
{
    // A minimal but real PNG: 8-byte signature followed by the start of an IHDR chunk.
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    [Fact]
    public async Task GetTicketAttachment_ReturnsBase64EncodedData()
    {
        var (client, _) = CreateClient(PngBytes, "image/png");

        var block = await AttachmentTools.GetTicketAttachment(
            client,
            "https://acme.zendesk.com/attachments/token/abc/?name=shot.png");

        Assert.Equal("image/png", block.MimeType);

        // Data must be base64 text, not the raw bytes. Before the fix this held the
        // unencoded PNG, which clients rejected as an invalid base64 string.
        var base64 = Encoding.UTF8.GetString(block.Data.Span);
        var decoded = Convert.FromBase64String(base64);

        Assert.Equal(PngBytes, decoded);
    }

    [Fact]
    public async Task GetTicketAttachment_RoundTripsThroughDecodedData()
    {
        var (client, _) = CreateClient(PngBytes, "image/png");

        var block = await AttachmentTools.GetTicketAttachment(
            client,
            "https://acme.zendesk.com/attachments/token/abc/?name=shot.png");

        Assert.Equal(PngBytes, block.DecodedData.ToArray());
    }

    [Fact]
    public async Task GetTicketAttachment_Rejects_NonImage()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nnot an image");
        var (client, _) = CreateClient(pdf, "application/pdf");

        await Assert.ThrowsAsync<InvalidOperationException>(() => AttachmentTools.GetTicketAttachment(
            client,
            "https://acme.zendesk.com/attachments/token/abc/?name=doc.pdf"));
    }

    private static (ZendeskClient Client, BinaryHandler Handler) CreateClient(byte[] body, string contentType)
    {
        var handler = new BinaryHandler { Body = body, ContentType = contentType };
        var factory = new StubHttpClientFactory(handler);
        var options = Options.Create(new ZendeskOptions
        {
            Subdomain = "acme",
            Email = "agent@acme.com",
            ApiToken = "secret-token",
        });
        return (new ZendeskClient(factory, options), handler);
    }

    private sealed class BinaryHandler : HttpMessageHandler
    {
        public byte[] Body { get; set; } = [];
        public string ContentType { get; set; } = "application/octet-stream";
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(Body);
            content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);

            return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = content });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
