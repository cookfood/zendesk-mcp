using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class CommentTools
{
    [McpServerTool(Name = "get_ticket_comments"), Description("Retrieve all comments (conversation) for a ticket by its ID.")]
    public static Task<string> GetTicketComments(
        ZendeskClient client,
        [Description("The ID of the ticket whose comments to retrieve.")] long ticketId,
        CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, $"tickets/{ticketId}/comments.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_ticket_comment"), Description("Add a comment to an existing ticket. Comments are public by default.")]
    public static Task<string> CreateTicketComment(
        ZendeskClient client,
        [Description("The ID of the ticket to comment on.")] long ticketId,
        [Description("The comment text to add.")] string comment,
        [Description("Whether the comment is public (visible to the requester). Default true.")] bool @public = true,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["ticket"] = new JsonObject
            {
                ["comment"] = new JsonObject
                {
                    ["body"] = comment,
                    ["public"] = @public,
                },
            },
        };
        return client.SendAsync(HttpMethod.Put, $"tickets/{ticketId}.json", jsonBody: body.ToJsonString(), cancellationToken: cancellationToken);
    }
}
