using System.ComponentModel;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class OrganizationTools
{
    [McpServerTool(Name = "get_organization"), Description("Retrieve a single organization by ID.")]
    public static Task<string> GetOrganization(
        ZendeskClient client,
        [Description("The ID of the organization to retrieve.")] long organizationId,
        CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, $"organizations/{organizationId}.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_organizations"), Description("List organizations, with pagination.")]
    public static Task<string> ListOrganizations(
        ZendeskClient client,
        [Description("Page number (1-based). Default 1.")] int page = 1,
        [Description("Organizations per page (max 100). Default 25.")] int perPage = 25,
        CancellationToken cancellationToken = default)
    {
        var query = ToolHelpers.Query(("page", page.ToString()), ("per_page", ToolHelpers.ClampPerPage(perPage).ToString()));
        return client.SendAsync(HttpMethod.Get, "organizations.json", query, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "search_organizations"), Description("Search organizations by name or other attributes.")]
    public static Task<string> SearchOrganizations(
        ZendeskClient client,
        [Description("The search term (the 'type:organization' scope is added for you).")] string query,
        CancellationToken cancellationToken = default)
    {
        var scoped = query.Contains("type:", StringComparison.OrdinalIgnoreCase) ? query : $"type:organization {query}";
        return client.SendAsync(HttpMethod.Get, "search.json", ToolHelpers.Query(("query", scoped)), cancellationToken: cancellationToken);
    }
}
