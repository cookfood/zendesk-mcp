using System.ComponentModel;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class GroupTools
{
    [McpServerTool(Name = "list_groups"), Description("List all agent groups (e.g. to find the 'Dev Team' group ID).")]
    public static Task<string> ListGroups(ZendeskClient client, CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, "groups.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_group"), Description("Retrieve a single agent group by ID.")]
    public static Task<string> GetGroup(
        ZendeskClient client,
        [Description("The ID of the group to retrieve.")] long groupId,
        CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, $"groups/{groupId}.json", cancellationToken: cancellationToken);
}

[McpServerToolType]
public static class ViewTools
{
    [McpServerTool(Name = "list_views"), Description("List the saved views (the filters you see in the Zendesk agent UI).")]
    public static Task<string> ListViews(ZendeskClient client, CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, "views.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_view"), Description("Retrieve a single view by ID.")]
    public static Task<string> GetView(
        ZendeskClient client,
        [Description("The ID of the view to retrieve.")] long viewId,
        CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, $"views/{viewId}.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_view_tickets"), Description("Execute a view and return the tickets it contains.")]
    public static Task<string> GetViewTickets(
        ZendeskClient client,
        [Description("The ID of the view to execute.")] long viewId,
        CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, $"views/{viewId}/tickets.json", cancellationToken: cancellationToken);
}

[McpServerToolType]
public static class MacroTools
{
    [McpServerTool(Name = "list_macros"), Description("List the macros (canned ticket actions).")]
    public static Task<string> ListMacros(ZendeskClient client, CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, "macros.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_macro"), Description("Retrieve a single macro by ID.")]
    public static Task<string> GetMacro(
        ZendeskClient client,
        [Description("The ID of the macro to retrieve.")] long macroId,
        CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, $"macros/{macroId}.json", cancellationToken: cancellationToken);
}

[McpServerToolType]
public static class HelpCenterTools
{
    [McpServerTool(Name = "search_articles"), Description("Search Help Centre (knowledge base) articles by text.")]
    public static Task<string> SearchArticles(
        ZendeskClient client,
        [Description("The search query.")] string query,
        CancellationToken cancellationToken = default)
        => client.SendAsync(HttpMethod.Get, "help_center/articles/search.json", ToolHelpers.Query(("query", query)), cancellationToken: cancellationToken);
}
