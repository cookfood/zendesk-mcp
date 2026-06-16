using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class AttachmentTools
{
    [McpServerTool(Name = "get_ticket_attachment"), Description(
        "Download a ticket attachment by its content_url (from get_ticket_comments) and return it as an image. " +
        "Only image attachments up to 10 MB are supported (JPEG, PNG, GIF, WEBP).")]
    public static async Task<ImageContentBlock> GetTicketAttachment(
        ZendeskClient client,
        [Description("The content_url of the attachment, as returned by get_ticket_comments.")] string contentUrl,
        CancellationToken cancellationToken = default)
    {
        var (data, contentType) = await client.DownloadAttachmentAsync(contentUrl, cancellationToken);
        var mime = AttachmentValidator.Validate(data, contentType);

        return new ImageContentBlock
        {
            Data = data,
            MimeType = mime,
        };
    }
}
