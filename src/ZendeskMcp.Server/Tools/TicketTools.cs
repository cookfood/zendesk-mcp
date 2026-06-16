using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class TicketTools
{
    [McpServerTool(Name = "get_ticket"), Description("Retrieve a single Zendesk ticket by its ID.")]
    public static Task<string> GetTicket(
        ZendeskClient client,
        [Description("The ID of the ticket to retrieve.")] long ticketId,
        CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, $"tickets/{ticketId}.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_tickets"), Description("List the most recent tickets, with pagination and sorting.")]
    public static Task<string> GetTickets(
        ZendeskClient client,
        [Description("Page number (1-based). Default 1.")] int page = 1,
        [Description("Tickets per page (max 100). Default 25.")] int perPage = 25,
        [Description("Field to sort by: created_at, updated_at, priority or status. Default created_at.")] string sortBy = "created_at",
        [Description("Sort order: asc or desc. Default desc.")] string sortOrder = "desc",
        CancellationToken cancellationToken = default)
    {
        var query = ToolHelpers.Query(
            ("page", page.ToString()),
            ("per_page", ToolHelpers.ClampPerPage(perPage).ToString()),
            ("sort_by", sortBy),
            ("sort_order", sortOrder));
        return client.SendAsync(HttpMethod.Get, "tickets.json", query, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "search_tickets"), Description(
        "Search tickets using the Zendesk search query language. Your query is automatically scoped to tickets. " +
        "Examples: 'group:\"Dev Team\" status:open', 'assignee:none status:new', 'tags:gift_card status<solved'.")]
    public static Task<string> SearchTickets(
        ZendeskClient client,
        [Description("The search query (the 'type:ticket' scope is added for you).")] string query,
        [Description("Optional field to sort by: created_at, updated_at, priority, status, ticket_type.")] string? sortBy = null,
        [Description("Optional sort order: asc or desc.")] string? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        var scoped = query.Contains("type:", StringComparison.OrdinalIgnoreCase) ? query : $"type:ticket {query}";
        var q = ToolHelpers.Query(
            ("query", scoped),
            ("sort_by", sortBy),
            ("sort_order", sortOrder));
        return client.SendAsync(HttpMethod.Get, "search.json", q, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "create_ticket"), Description("Create a new Zendesk ticket.")]
    public static Task<string> CreateTicket(
        ZendeskClient client,
        [Description("Ticket subject.")] string subject,
        [Description("The ticket description / first comment body.")] string description,
        [Description("Optional priority: low, normal, high or urgent.")] string? priority = null,
        [Description("Optional type: problem, incident, question or task.")] string? type = null,
        [Description("Optional requester user ID.")] long? requesterId = null,
        [Description("Optional assignee user ID.")] long? assigneeId = null,
        [Description("Optional comma-separated tags.")] string? tags = null,
        [Description("Optional custom fields as a JSON array, e.g. [{\"id\":123,\"value\":\"x\"}].")] string? customFieldsJson = null,
        CancellationToken cancellationToken = default)
    {
        var ticket = new JsonObject
        {
            ["subject"] = subject,
            ["comment"] = new JsonObject { ["body"] = description },
        };
        SetIfPresent(ticket, "priority", priority);
        SetIfPresent(ticket, "type", type);
        if (requesterId is { } r) ticket["requester_id"] = r;
        if (assigneeId is { } a) ticket["assignee_id"] = a;
        SetTags(ticket, tags);
        SetCustomFields(ticket, customFieldsJson);

        var body = new JsonObject { ["ticket"] = ticket };
        return client.SendAsync(HttpMethod.Post, "tickets.json", jsonBody: body.ToJsonString(), cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "update_ticket"), Description("Update fields on an existing ticket (status, priority, assignee, tags, etc.).")]
    public static Task<string> UpdateTicket(
        ZendeskClient client,
        [Description("The ID of the ticket to update.")] long ticketId,
        [Description("Optional new subject.")] string? subject = null,
        [Description("Optional status: new, open, pending, hold, solved or closed.")] string? status = null,
        [Description("Optional priority: low, normal, high or urgent.")] string? priority = null,
        [Description("Optional type: problem, incident, question or task.")] string? type = null,
        [Description("Optional assignee user ID.")] long? assigneeId = null,
        [Description("Optional requester user ID.")] long? requesterId = null,
        [Description("Optional comma-separated tags (replaces existing tags).")] string? tags = null,
        [Description("Optional due date (ISO 8601) for tasks.")] string? dueAt = null,
        [Description("Optional custom fields as a JSON array, e.g. [{\"id\":123,\"value\":\"x\"}].")] string? customFieldsJson = null,
        CancellationToken cancellationToken = default)
    {
        var ticket = new JsonObject();
        SetIfPresent(ticket, "subject", subject);
        SetIfPresent(ticket, "status", status);
        SetIfPresent(ticket, "priority", priority);
        SetIfPresent(ticket, "type", type);
        if (assigneeId is { } a) ticket["assignee_id"] = a;
        if (requesterId is { } r) ticket["requester_id"] = r;
        SetIfPresent(ticket, "due_at", dueAt);
        SetTags(ticket, tags);
        SetCustomFields(ticket, customFieldsJson);

        var body = new JsonObject { ["ticket"] = ticket };
        return client.SendAsync(HttpMethod.Put, $"tickets/{ticketId}.json", jsonBody: body.ToJsonString(), cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "delete_ticket"), Description("Delete a ticket by its ID. This cannot be undone.")]
    public static async Task<string> DeleteTicket(
        ZendeskClient client,
        [Description("The ID of the ticket to delete.")] long ticketId,
        CancellationToken cancellationToken = default)
    {
        await client.SendAsync(HttpMethod.Delete, $"tickets/{ticketId}.json", cancellationToken: cancellationToken);
        return $"{{\"deleted\":true,\"ticket_id\":{ticketId}}}";
    }

    private static void SetIfPresent(JsonObject obj, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) obj[key] = value;
    }

    private static void SetTags(JsonObject ticket, string? tags)
    {
        var parsed = ToolHelpers.SplitCsv(tags);
        if (parsed is null) return;
        var array = new JsonArray();
        foreach (var tag in parsed) array.Add(tag);
        ticket["tags"] = array;
    }

    private static void SetCustomFields(JsonObject ticket, string? customFieldsJson)
    {
        var parsed = ToolHelpers.ParseJson(customFieldsJson);
        if (parsed is not null) ticket["custom_fields"] = parsed;
    }
}
