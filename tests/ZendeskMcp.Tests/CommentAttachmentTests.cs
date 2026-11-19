using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Tools;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class CommentAttachmentTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    // ---- create_ticket_comment ----

    [Fact]
    public async Task CreateTicketComment_UploadsEachFile_ThenAttachesTheTokens()
    {
        var (client, handler) = CreateClient();

        await CommentTools.CreateTicketComment(client, 42, "Logs attached.", @public: false, attachments:
        [
            new CommentAttachment { FileName = "error.log", Content = "boom" },
            new CommentAttachment { FileName = "orders", Content = "id,total\n1,9.99", Format = "xlsx" },
        ]);

        Assert.Equal(3, handler.Requests.Count);

        var first = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, first.Method);
        Assert.Equal("https://acme.zendesk.com/api/v2/uploads.json?filename=error.log", first.Url);
        Assert.Equal("text/plain", first.ContentType);
        Assert.Equal("boom", Encoding.UTF8.GetString(first.Body));

        var second = handler.Requests[1];
        Assert.EndsWith("uploads.json?filename=orders.xlsx", second.Url);
        Assert.Equal(XlsxWriter.ContentType, second.ContentType);
        Assert.Equal((byte)'P', second.Body[0]); // zip signature "PK"

        var update = handler.Requests[2];
        Assert.Equal(HttpMethod.Put, update.Method);
        Assert.EndsWith("tickets/42.json", update.Url);
        var comment = JsonNode.Parse(update.Body)!["ticket"]!["comment"]!;
        Assert.Equal("Logs attached.", comment["body"]!.GetValue<string>());
        Assert.False(comment["public"]!.GetValue<bool>());
        Assert.Equal(["token-1", "token-2"], comment["uploads"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public async Task CreateTicketComment_WithoutAttachments_SendsNoUploads()
    {
        var (client, handler) = CreateClient();

        await CommentTools.CreateTicketComment(client, 42, "Hello");

        var request = Assert.Single(handler.Requests);
        var comment = JsonNode.Parse(request.Body)!["ticket"]!["comment"]!.AsObject();
        Assert.False(comment.ContainsKey("uploads"));
    }

    [Fact]
    public async Task CreateTicketComment_InvalidAttachment_UploadsNothing()
    {
        var (client, handler) = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() => CommentTools.CreateTicketComment(client, 42, "x", attachments:
        [
            new CommentAttachment { FileName = "ok.txt", Content = "fine" },
            new CommentAttachment { FileName = "bad.bin", Content = "not base64!!", Format = "base64" },
        ]));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void CreateTicketComment_Schema_ExposesAttachmentFields()
    {
        var method = typeof(CommentTools).GetMethod(nameof(CommentTools.CreateTicketComment))!;
        var tool = McpServerTool.Create(method);

        var schema = tool.ProtocolTool.InputSchema;
        var items = schema.GetProperty("properties").GetProperty("attachments").GetProperty("items");
        var properties = items.GetProperty("properties");

        Assert.True(properties.TryGetProperty("fileName", out _));
        Assert.True(properties.TryGetProperty("content", out _));
        Assert.True(properties.TryGetProperty("format", out _));

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("attachments", required);
    }

    // ---- OutgoingAttachments ----

    [Fact]
    public void Prepare_Csv_AddsByteOrderMark_SoExcelReadsPoundSigns()
    {
        var file = OutgoingAttachments.Prepare(new CommentAttachment { FileName = "refunds.csv", Content = "amount\n£5" });

        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal([0xEF, 0xBB, 0xBF], file.Data[..3]);
        Assert.Equal("amount\n£5", Encoding.UTF8.GetString(file.Data[3..]));
    }

    [Fact]
    public void Prepare_Base64_Decodes()
    {
        var bytes = new byte[] { 1, 2, 3, 255 };
        var file = OutgoingAttachments.Prepare(new CommentAttachment
        {
            FileName = "blob.zip",
            Content = Convert.ToBase64String(bytes),
            Format = "BASE64",
        });

        Assert.Equal(bytes, file.Data);
        Assert.Equal("application/zip", file.ContentType);
    }

    [Fact]
    public void Prepare_StripsDirectories_FromFileName()
    {
        var file = OutgoingAttachments.Prepare(new CommentAttachment { FileName = @"C:\temp\..\query.sql", Content = "SELECT 1" });
        Assert.Equal("query.sql", file.FileName);
    }

    [Theory]
    [InlineData("", "x", "text")]
    [InlineData("a.txt", "", "text")]
    [InlineData("a.txt", "x", "pdf")]
    [InlineData("a.bin", "@@@", "base64")]
    public void Prepare_RejectsBadInput(string fileName, string content, string format)
    {
        Assert.Throws<ArgumentException>(() =>
            OutgoingAttachments.Prepare(new CommentAttachment { FileName = fileName, Content = content, Format = format }));
    }

    [Fact]
    public void Prepare_RejectsOversizedFile()
    {
        var content = new string('a', OutgoingAttachments.MaxBytes + 1);
        Assert.Throws<ArgumentException>(() =>
            OutgoingAttachments.Prepare(new CommentAttachment { FileName = "big.log", Content = content }));
    }

    [Fact]
    public void PrepareAll_RejectsTooManyFiles()
    {
        var files = Enumerable.Range(0, OutgoingAttachments.MaxCount + 1)
            .Select(i => new CommentAttachment { FileName = $"{i}.txt", Content = "x" })
            .ToArray();
        Assert.Throws<ArgumentException>(() => OutgoingAttachments.PrepareAll(files));
    }

    // ---- CSV and XLSX ----

    [Fact]
    public void CsvParser_HandlesQuotesEmbeddedCommasAndNewlines()
    {
        var rows = CsvParser.Parse("name,note\r\n\"Smith, J\",\"said \"\"hi\"\"\nthen left\"\r\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["Smith, J", "said \"hi\"\nthen left"], rows[1]);
    }

    [Fact]
    public void CsvParser_ReadsTabSeparated_FromSqlResults()
    {
        var rows = CsvParser.Parse("OrderId\tTotal\n101\t12.50");
        Assert.Equal(["101", "12.50"], rows[1]);
    }

    [Fact]
    public void CsvParser_KeepsEmptyTrailingFields()
    {
        var rows = CsvParser.Parse("a,b,\n");
        Assert.Equal(["a", "b", ""], Assert.Single(rows));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ColumnName_UsesExcelLetters(int index, string expected) =>
        Assert.Equal(expected, XlsxWriter.ColumnName(index));

    [Theory]
    [InlineData("12", true)]
    [InlineData("-3.25", true)]
    [InlineData("0.5", true)]
    [InlineData("00123", false)]
    [InlineData("07700900123", false)]
    [InlineData("1e5", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("12.", false)]
    public void IsPlainNumber_KeepsIdsAsText(string value, bool expected) =>
        Assert.Equal(expected, XlsxWriter.IsPlainNumber(value));

    [Fact]
    public void Xlsx_HasEveryPackagePart_AndWellFormedXml()
    {
        using var zip = OpenWorkbook("a,b\n1,2");

        string[] parts =
        [
            "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
            "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml",
        ];
        foreach (var part in parts)
        {
            var entry = zip.GetEntry(part);
            Assert.NotNull(entry);
            using var stream = entry.Open();
            XDocument.Load(stream); // throws if not well-formed
        }
    }

    [Fact]
    public void Xlsx_WritesBoldHeader_NumbersAsNumbers_AndFormulasAsText()
    {
        using var zip = OpenWorkbook("Order,Total,Phone,Note\n101,9.99,07700900123,=SUM(A1:A9)");
        var sheet = LoadSheet(zip);
        var cells = sheet.Descendants(Main + "c").ToDictionary(c => (string)c.Attribute("r")!);

        Assert.Equal("1", (string?)cells["A1"].Attribute("s"));
        Assert.Equal("Order", cells["A1"].Value);

        Assert.Null(cells["A2"].Attribute("t"));
        Assert.Equal("101", cells["A2"].Element(Main + "v")!.Value);
        Assert.Equal("9.99", cells["B2"].Element(Main + "v")!.Value);

        Assert.Equal("inlineStr", (string?)cells["C2"].Attribute("t"));
        Assert.Equal("07700900123", cells["C2"].Value);

        Assert.Equal("inlineStr", (string?)cells["D2"].Attribute("t"));
        Assert.Null(cells["D2"].Element(Main + "f"));
        Assert.Equal("=SUM(A1:A9)", cells["D2"].Value);

        Assert.NotNull(sheet.Descendants(Main + "pane").SingleOrDefault());
    }

    [Fact]
    public void Xlsx_DropsControlCharacters_ThatXmlCannotHold()
    {
        using var zip = OpenWorkbook("h\n\"a\u0001b\"");
        var cell = LoadSheet(zip).Descendants(Main + "c").Single(c => (string)c.Attribute("r")! == "A2");
        Assert.Equal("ab", cell.Value);
    }

    private static ZipArchive OpenWorkbook(string csv) =>
        new(new MemoryStream(XlsxWriter.FromCsv(csv)), ZipArchiveMode.Read);

    private static XDocument LoadSheet(ZipArchive zip)
    {
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(stream);
    }

    // ---- helpers ----

    private static (ZendeskClient Client, RecordingHandler Handler) CreateClient()
    {
        var handler = new RecordingHandler();
        var options = Options.Create(new ZendeskOptions
        {
            Subdomain = "acme",
            Email = "agent@acme.com",
            ApiToken = "secret-token",
        });
        return (new ZendeskClient(new StubHttpClientFactory(handler), options), handler);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Url, string? ContentType, byte[] Body);

    /// <summary>Records each request and answers uploads with a numbered token.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                body));

            var json = request.RequestUri!.AbsolutePath.EndsWith("uploads.json")
                ? JsonSerializer.Serialize(new { upload = new { token = $"token-{Requests.Count(r => r.Url.Contains("uploads.json"))}" } })
                : "{\"ticket\":{\"id\":42}}";

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
