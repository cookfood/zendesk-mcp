using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class CommentTools
{
    [McpServerTool(Name = "get_ticket_comments"), Description(
        "Retrieve all comments (conversation) for a ticket by its ID. Inline (pasted) " +
        "screenshots embedded in a comment body are surfaced in that comment's " +
        "'attachments' array with \"inline\": true and a content_url; fetch them the same " +
        "way as any attachment, via get_ticket_attachment.")]
    public static async Task<string> GetTicketComments(
        ZendeskClient client,
        [Description("The ID of the ticket whose comments to retrieve.")] long ticketId,
        CancellationToken cancellationToken)
    {
        var json = await client.SendAsync(HttpMethod.Get, $"tickets/{ticketId}/comments.json", cancellationToken: cancellationToken);
        return InlineImageEnricher.Enrich(json);
    }

    [McpServerTool(Name = "create_ticket_comment"), Description(
        "Add a comment to an existing ticket. Comments are public by default. Files can be attached " +
        "in the same call via 'attachments': pass logs, CSV, SQL output and other text as-is, or use " +
        "format 'xlsx' with CSV text to attach an Excel workbook. The server uploads them to Zendesk " +
        "and attaches them to this comment; no separate upload step is needed.")]
    public static async Task<string> CreateTicketComment(
        ZendeskClient client,
        [Description("The ID of the ticket to comment on.")] long ticketId,
        [Description("The comment text to add.")] string comment,
        [Description("Whether the comment is public (visible to the requester). Default true.")] bool @public = true,
        [Description("Optional files to attach to the comment (up to 10, 10 MB each).")]
        CommentAttachment[]? attachments = null,
        CancellationToken cancellationToken = default)
    {
        // Validate and build every file before uploading any of them.
        var prepared = OutgoingAttachments.PrepareAll(attachments);

        var commentBody = new JsonObject
        {
            ["body"] = comment,
            ["public"] = @public,
        };

        if (prepared.Count > 0)
        {
            var tokens = new JsonArray();
            foreach (var file in prepared)
            {
                tokens.Add(await client.UploadAsync(file.FileName, file.Data, file.ContentType, cancellationToken));
            }
            commentBody["uploads"] = tokens;
        }

        var body = new JsonObject
        {
            ["ticket"] = new JsonObject { ["comment"] = commentBody },
        };
        return await client.SendAsync(HttpMethod.Put, $"tickets/{ticketId}.json", jsonBody: body.ToJsonString(), cancellationToken: cancellationToken);
    }
}
