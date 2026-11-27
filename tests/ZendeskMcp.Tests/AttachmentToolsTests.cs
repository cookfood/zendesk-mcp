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
        var result = await Call(PngBytes, "image/png", "shot.png");
        var block = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));

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
        var result = await Call(PngBytes, "image/png", "shot.png");
        var block = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));

        Assert.Equal(PngBytes, block.DecodedData.ToArray());
    }

    [Fact]
    public async Task GetTicketAttachment_Rejects_ImageWhoseBytesDoNotMatch()
    {
        var notPng = Encoding.ASCII.GetBytes("<script>alert(1)</script>");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(notPng, "image/png", "shot.png"));
    }

    [Fact]
    public async Task GetTicketAttachment_ReturnsImage_WhenServedAsOctetStream()
    {
        var result = await Call(PngBytes, "application/octet-stream", "shot.png");
        var block = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));

        Assert.Equal("image/png", block.MimeType);
    }

    [Fact]
    public async Task GetTicketAttachment_ReturnsCsvAsText()
    {
        var csv = "order_id,amount\n1001,12.50\n1002,7.00\n";
        var result = await Call(Encoding.UTF8.GetBytes(csv), "text/csv", "refunds.csv");
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        Assert.Contains("Attachment: refunds.csv (text/csv", text);
        Assert.EndsWith(csv, text);
    }

    [Fact]
    public async Task GetTicketAttachment_DecodesNonUtf8TextAsWindows1252()
    {
        // 0x80 is the euro sign in Windows-1252 and invalid on its own in UTF-8.
        byte[] bytes = [.. Encoding.ASCII.GetBytes("total,"), 0x80, .. Encoding.ASCII.GetBytes("5\n")];
        var result = await Call(bytes, "text/csv", "export.csv");
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        Assert.Contains("total,€5", text);
        Assert.Contains("Windows-1252", text);
    }

    [Fact]
    public async Task GetTicketAttachment_ReturnsXlsxSheetsAsCsv()
    {
        var workbook = XlsxWriter.FromCsv("order_id,customer,amount\n00123,\"Smith, J\",12.5\n");
        var result = await Call(workbook, "application/octet-stream", "refunds.xlsx");
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        Assert.Contains("Converted from Excel", text);
        Assert.Contains("## Sheet 1: Sheet1 (2 non-empty rows)", text);
        Assert.Contains("order_id,customer,amount\n00123,\"Smith, J\",12.5\n", text);
    }

    [Fact]
    public async Task GetTicketAttachment_ReturnsPdfAsEmbeddedBase64()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nnot really a pdf");
        var url = "https://acme.zendesk.com/attachments/token/abc/?name=invoice.pdf";
        var result = await Call(pdf, "application/pdf", "invoice.pdf");

        Assert.Equal(2, result.Content.Count);
        Assert.Contains("invoice.pdf", Assert.IsType<TextContentBlock>(result.Content[0]).Text);

        var resource = Assert.IsType<EmbeddedResourceBlock>(result.Content[1]);
        var blob = Assert.IsType<BlobResourceContents>(resource.Resource);
        Assert.Equal("application/pdf", blob.MimeType);
        Assert.Equal(url, blob.Uri);
        Assert.Equal(pdf, blob.DecodedData.ToArray());
    }

    [Fact]
    public async Task GetTicketAttachment_PagesLongText()
    {
        var line = new string('x', 99) + "\n";
        var body = string.Concat(Enumerable.Repeat(line, 500)); // 50,000 characters
        var bytes = Encoding.UTF8.GetBytes(body);

        var first = Assert.IsType<TextContentBlock>(Assert.Single((await Call(bytes, "text/plain", "big.log")).Content)).Text;
        Assert.Contains($"Characters 0 to {AttachmentTools.TextPageChars} of {body.Length}.", first);
        Assert.Contains($"offset={AttachmentTools.TextPageChars}", first);

        var second = Assert.IsType<TextContentBlock>(Assert.Single(
            (await Call(bytes, "text/plain", "big.log", AttachmentTools.TextPageChars)).Content)).Text;
        Assert.Contains("This is the last part.", second);

        static string Body(string page) => page[(page.IndexOf("---\n", StringComparison.Ordinal) + 4)..];
        Assert.Equal(body, Body(first) + Body(second));
    }

    [Fact]
    public async Task GetTicketAttachment_Rejects_OffsetPastTheEnd()
    {
        var bytes = Encoding.UTF8.GetBytes("short");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Call(bytes, "text/plain", "a.txt", 100));
    }

    private static Task<CallToolResult> Call(byte[] body, string contentType, string name, int offset = 0)
    {
        var (client, _) = CreateClient(body, contentType);
        return AttachmentTools.GetTicketAttachment(
            client,
            $"https://acme.zendesk.com/attachments/token/abc/?name={name}",
            offset);
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
