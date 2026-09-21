using System.Text.Json.Nodes;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class InlineImageEnricherTests
{
    [Fact]
    public void Enrich_SurfacesInlineImages_AsAttachments()
    {
        // A comment with two pasted screenshots in its html_body and no real attachments,
        // mirroring an emailed comment.
        var json = """
        {
          "comments": [
            {
              "id": 1,
              "html_body": "<div>See <img style=\"max-width:567px\" src=\"https://acme.zendesk.com/attachments/token/AAA/?name=image.png\"> and <img width=\"10\" src=\"https://acme.zendesk.com/attachments/token/BBB/?name=chart.jpg\"></div>",
              "attachments": []
            }
          ]
        }
        """;

        var enriched = JsonNode.Parse(InlineImageEnricher.Enrich(json))!;
        var attachments = enriched["comments"]![0]!["attachments"]!.AsArray();

        Assert.Equal(2, attachments.Count);

        Assert.Equal("https://acme.zendesk.com/attachments/token/AAA/?name=image.png", (string?)attachments[0]!["content_url"]);
        Assert.Equal("image.png", (string?)attachments[0]!["file_name"]);
        Assert.Equal("image/png", (string?)attachments[0]!["content_type"]);
        Assert.True((bool)attachments[0]!["inline"]!);
        Assert.True((bool)attachments[0]!["derived_from_body"]!);

        Assert.Equal("chart.jpg", (string?)attachments[1]!["file_name"]);
        Assert.Equal("image/jpeg", (string?)attachments[1]!["content_type"]);
    }

    [Fact]
    public void Enrich_DoesNotDuplicate_ImagesAlreadyListedAsAttachments()
    {
        // The same token appears both as a real attachment and as an <img> in the body.
        var json = """
        {
          "comments": [
            {
              "id": 1,
              "html_body": "<img src=\"https://acme.zendesk.com/attachments/token/AAA/?name=image.png\">",
              "attachments": [
                { "file_name": "image.png", "content_url": "https://acme.zendesk.com/attachments/token/AAA/?name=image.png", "inline": true }
              ]
            }
          ]
        }
        """;

        var enriched = JsonNode.Parse(InlineImageEnricher.Enrich(json))!;
        var attachments = enriched["comments"]![0]!["attachments"]!.AsArray();

        Assert.Single(attachments);
        Assert.Null(attachments[0]!["derived_from_body"]);
    }

    [Fact]
    public void Enrich_IgnoresExternalImages()
    {
        // An e-mail signature logo and a tracking pixel are not on the attachment endpoint.
        var json = """
        {
          "comments": [
            {
              "id": 1,
              "html_body": "<img src=\"https://cdn.example.com/logo.png\"><img src=\"https://tracker.example.com/pixel.gif?id=42\">",
              "attachments": []
            }
          ]
        }
        """;

        var enriched = JsonNode.Parse(InlineImageEnricher.Enrich(json))!;
        var attachments = enriched["comments"]![0]!["attachments"]!.AsArray();

        Assert.Empty(attachments);
    }

    [Fact]
    public void Enrich_DedupesRepeatedImgTag()
    {
        var json = """
        {
          "comments": [
            {
              "id": 1,
              "html_body": "<img src=\"https://acme.zendesk.com/attachments/token/AAA/?name=image.png\"><img src=\"https://acme.zendesk.com/attachments/token/AAA/?name=image.png\">"
            }
          ]
        }
        """;

        var enriched = JsonNode.Parse(InlineImageEnricher.Enrich(json))!;
        var attachments = enriched["comments"]![0]!["attachments"]!.AsArray();

        Assert.Single(attachments);
    }

    [Fact]
    public void Enrich_FallsBackToMarkdownBody_WhenNoHtml()
    {
        // A comment with only the Markdown body (no html_body): the ![](...) form still
        // carries the token URL and must be surfaced, since the html_body fallback is off.
        var json = """
        {
          "comments": [
            {
              "id": 1,
              "body": "Here it is ![](https://acme.zendesk.com/attachments/token/AAA/?name=image.png)"
            }
          ]
        }
        """;

        var enriched = JsonNode.Parse(InlineImageEnricher.Enrich(json))!;
        var attachments = enriched["comments"]![0]!["attachments"]!.AsArray();

        Assert.Single(attachments);
        Assert.Equal("https://acme.zendesk.com/attachments/token/AAA/?name=image.png", (string?)attachments[0]!["content_url"]);
        Assert.True((bool)attachments[0]!["inline"]!);
    }

    [Fact]
    public void Enrich_ReturnsInputUnchanged_OnInvalidJson()
    {
        const string garbage = "not json at all";
        Assert.Equal(garbage, InlineImageEnricher.Enrich(garbage));
    }

    [Fact]
    public void Enrich_ReturnsInputUnchanged_WhenNoComments()
    {
        const string json = """{"count":0}""";
        Assert.Equal(json, InlineImageEnricher.Enrich(json));
    }

    [Fact]
    public void ExtractInlineImageUrls_ReturnsUrlsInOrder()
    {
        const string html =
            "<img src=\"https://acme.zendesk.com/attachments/token/AAA/?name=a.png\">" +
            "<p>text</p>" +
            "<img src='https://acme.zendesk.com/attachments/token/BBB/?name=b.png'>";

        var urls = InlineImageEnricher.ExtractInlineImageUrls(html);

        Assert.Equal(
            [
                "https://acme.zendesk.com/attachments/token/AAA/?name=a.png",
                "https://acme.zendesk.com/attachments/token/BBB/?name=b.png",
            ],
            urls);
    }
}
