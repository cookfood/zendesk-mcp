using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class AttachmentTools
{
    /// <summary>
    /// Characters of text returned per call. Kept well under the 25,000-token default
    /// limit Claude Code puts on a single MCP tool result.
    /// </summary>
    internal const int TextPageChars = 40_000;

    [McpServerTool(Name = "get_ticket_attachment"), Description(
        "Download a ticket attachment by its content_url (from get_ticket_comments). Up to 10 MB. " +
        "Images (JPEG, PNG, GIF, WEBP) come back as images. Text files (CSV, TXT, JSON, XML, logs) come back as text, " +
        "and Excel .xlsx workbooks come back as text with each sheet as CSV. Long text is returned " +
        "40,000 characters at a time: call again with the offset given at the top of the result for the next part. " +
        "Anything else (PDF, Word, zip) comes back as a base64 embedded resource.")]
    public static async Task<CallToolResult> GetTicketAttachment(
        ZendeskClient client,
        [Description("The content_url of the attachment, as returned by get_ticket_comments.")] string contentUrl,
        [Description("For text results, the character to start from. Leave at 0 for the first part.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var (data, contentType) = await client.DownloadAttachmentAsync(contentUrl, cancellationToken);
        var attachment = AttachmentContent.From(data, contentType, contentUrl);

        return attachment.Kind switch
        {
            // FromBytes takes the raw bytes and base64-encodes them. Assigning the raw bytes
            // straight to ImageContentBlock.Data instead puts unencoded bytes where the protocol
            // expects base64, and every client rejects the block as an invalid base64 string.
            AttachmentContent.Kind.Image => new CallToolResult
            {
                Content = [ImageContentBlock.FromBytes(attachment.Data, attachment.MimeType)],
            },
            AttachmentContent.Kind.Text => new CallToolResult
            {
                Content = [new TextContentBlock { Text = TextPage(attachment, offset) }],
            },
            _ => new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = $"{Describe(attachment)}\n{attachment.Note}" },
                    new EmbeddedResourceBlock
                    {
                        Resource = BlobResourceContents.FromBytes(attachment.Data, contentUrl, attachment.MimeType),
                    },
                ],
            },
        };
    }

    internal static string TextPage(AttachmentContent.Result attachment, int offset)
    {
        var text = attachment.Text ?? string.Empty;
        if (offset < 0 || (offset > 0 && offset >= text.Length))
            throw new ArgumentOutOfRangeException(nameof(offset), $"offset must be between 0 and {Math.Max(0, text.Length - 1)}; the text is {text.Length} characters long.");

        var end = Math.Min(text.Length, offset + TextPageChars);
        // Do not split a surrogate pair across two pages.
        if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;

        var header = new StringBuilder(Describe(attachment)).Append('\n');
        if (attachment.Note is not null) header.Append(attachment.Note).Append('\n');

        if (offset == 0 && end == text.Length)
        {
            header.Append($"Full text, {text.Length} characters.\n");
        }
        else
        {
            header.Append($"Characters {offset} to {end} of {text.Length}.");
            header.Append(end < text.Length
                ? $" More follows: call get_ticket_attachment again with offset={end}.\n"
                : " This is the last part.\n");
        }

        return header.Append("---\n").Append(text, offset, end - offset).ToString();
    }

    private static string Describe(AttachmentContent.Result attachment) =>
        $"Attachment: {attachment.FileName} ({attachment.MimeType}, {attachment.Data.Length} bytes)";
}
