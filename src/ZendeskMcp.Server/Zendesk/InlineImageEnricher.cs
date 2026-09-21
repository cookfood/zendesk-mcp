using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Surfaces inline (pasted) screenshots from a comments payload so they behave like
/// real attachments.
///
/// When an image is pasted into a comment, Zendesk embeds it in the comment's
/// <c>html_body</c> as an <c>&lt;img src="…/attachments/token/…"&gt;</c> tag but, on
/// some channels (notably e-mail), leaves the comment's <c>attachments</c> array
/// empty. The download endpoint accepts those token URLs perfectly well, so the only
/// gap is discovery: the model never sees a <c>content_url</c> to fetch.
///
/// <see cref="Enrich"/> parses each comment's HTML, and for every Zendesk
/// attachment-token image not already present as a real attachment, appends a
/// synthesised attachment object carrying a <c>content_url</c> and
/// <c>"inline": true</c>. From that point the model treats it exactly like any other
/// attachment and fetches it via get_ticket_attachment.
/// </summary>
public static partial class InlineImageEnricher
{
    // Matches an image reference in either form Zendesk uses: an <img src="URL"> tag in
    // html_body (groups 1/2, double/single quoted) or a Markdown ![](URL) in body (group 3).
    // A targeted match is more predictable here than a full HTML parse, and running both
    // forms through one regex keeps the matches in document order.
    [GeneratedRegex(
        """<img\b[^>]*?\bsrc\s*=\s*(?:"([^"]+)"|'([^']+)')|!\[[^\]]*\]\(\s*([^)\s]+)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImageRefRegex();

    /// <summary>
    /// Takes the raw JSON body from the comments endpoint and returns it with inline
    /// images surfaced as attachments. Any parsing problem returns <paramref name="commentsJson"/>
    /// unchanged: enrichment is best-effort and never blocks the underlying data.
    /// </summary>
    public static string Enrich(string commentsJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(commentsJson);
        }
        catch
        {
            return commentsJson;
        }

        if (root is not JsonObject obj || obj["comments"] is not JsonArray comments)
            return commentsJson;

        var changed = false;
        foreach (var comment in comments)
        {
            if (comment is JsonObject c && EnrichComment(c))
                changed = true;
        }

        return changed ? root.ToJsonString() : commentsJson;
    }

    private static bool EnrichComment(JsonObject comment)
    {
        var html = comment["html_body"]?.GetValue<string>();
        var body = comment["body"]?.GetValue<string>();
        var source = !string.IsNullOrEmpty(html) ? html : body;
        if (string.IsNullOrEmpty(source))
            return false;

        var urls = ExtractInlineImageUrls(source);
        if (urls.Count == 0)
            return false;

        // Preserve any existing attachments; only add inline images not already listed.
        if (comment["attachments"] is not JsonArray attachments)
        {
            attachments = new JsonArray();
            comment["attachments"] = attachments;
        }

        var existingTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var att in attachments)
        {
            if (att?["content_url"]?.GetValue<string>() is { } existingUrl && TokenOf(existingUrl) is { } token)
                existingTokens.Add(token);
        }

        var added = false;
        foreach (var url in urls)
        {
            var token = TokenOf(url);
            if (token is not null && !existingTokens.Add(token))
                continue; // already present as a real attachment or a duplicate img tag

            var name = NameOf(url) ?? "image";
            attachments.Add(new JsonObject
            {
                ["file_name"] = name,
                ["content_url"] = url,
                ["content_type"] = ContentTypeFromName(name),
                ["inline"] = true,
                // Marks this entry as reconstructed from the comment body rather than
                // returned by Zendesk, so consumers can tell the two apart.
                ["derived_from_body"] = true,
            });
            added = true;
        }

        return added;
    }

    /// <summary>
    /// Returns the distinct Zendesk attachment-token image URLs referenced in a comment
    /// body, in document order. External images (e-mail signatures, tracking pixels) are
    /// skipped: they are not on the attachment endpoint and could not be auth-fetched.
    /// </summary>
    public static List<string> ExtractInlineImageUrls(string source)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ImageRefRegex().Matches(source))
        {
            var url =
                match.Groups[1].Success ? match.Groups[1].Value :
                match.Groups[2].Success ? match.Groups[2].Value :
                match.Groups[3].Value;
            url = System.Net.WebUtility.HtmlDecode(url).Trim();
            if (!IsZendeskAttachmentUrl(url))
                continue;
            if (seen.Add(url))
                result.Add(url);
        }

        return result;
    }

    private static bool IsZendeskAttachmentUrl(string url) =>
        url.Contains("/attachments/token/", StringComparison.OrdinalIgnoreCase)
        && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private static string? TokenOf(string url)
    {
        const string marker = "/attachments/token/";
        var start = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = url.IndexOfAny(['/', '?'], start);
        var token = end < 0 ? url[start..] : url[start..end];
        return token.Length == 0 ? null : token;
    }

    private static string? NameOf(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0)
            return null;
        foreach (var pair in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            if (pair[..eq].Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        return null;
    }

    private static string? ContentTypeFromName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0)
            return null;
        return name[(dot + 1)..].ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            _ => null,
        };
    }
}
