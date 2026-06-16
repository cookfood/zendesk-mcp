using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class UserTools
{
    [McpServerTool(Name = "get_current_user"), Description("Get the user the API is authenticating as (the shared service account).")]
    public static Task<string> GetCurrentUser(ZendeskClient client, CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, "users/me.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_user"), Description("Retrieve a single user by ID.")]
    public static Task<string> GetUser(
        ZendeskClient client,
        [Description("The ID of the user to retrieve.")] long userId,
        CancellationToken cancellationToken)
        => client.SendAsync(HttpMethod.Get, $"users/{userId}.json", cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_users"), Description("List users, with pagination.")]
    public static Task<string> ListUsers(
        ZendeskClient client,
        [Description("Page number (1-based). Default 1.")] int page = 1,
        [Description("Users per page (max 100). Default 25.")] int perPage = 25,
        CancellationToken cancellationToken = default)
    {
        var query = ToolHelpers.Query(("page", page.ToString()), ("per_page", ToolHelpers.ClampPerPage(perPage).ToString()));
        return client.SendAsync(HttpMethod.Get, "users.json", query, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "search_users"), Description("Search users by name, e-mail, phone or other attributes.")]
    public static Task<string> SearchUsers(
        ZendeskClient client,
        [Description("The search term, e.g. an e-mail address or name.")] string query,
        CancellationToken cancellationToken = default)
        => client.SendAsync(HttpMethod.Get, "users/search.json", ToolHelpers.Query(("query", query)), cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_user"), Description("Create a new user.")]
    public static Task<string> CreateUser(
        ZendeskClient client,
        [Description("The user's full name.")] string name,
        [Description("The user's e-mail address.")] string email,
        [Description("Optional role: end-user, agent or admin. Default end-user.")] string? role = null,
        [Description("Optional phone number.")] string? phone = null,
        CancellationToken cancellationToken = default)
    {
        var user = new JsonObject { ["name"] = name, ["email"] = email };
        if (!string.IsNullOrWhiteSpace(role)) user["role"] = role;
        if (!string.IsNullOrWhiteSpace(phone)) user["phone"] = phone;
        var body = new JsonObject { ["user"] = user };
        return client.SendAsync(HttpMethod.Post, "users.json", jsonBody: body.ToJsonString(), cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "update_user"), Description("Update fields on an existing user.")]
    public static Task<string> UpdateUser(
        ZendeskClient client,
        [Description("The ID of the user to update.")] long userId,
        [Description("Optional new name.")] string? name = null,
        [Description("Optional new e-mail address.")] string? email = null,
        [Description("Optional new role: end-user, agent or admin.")] string? role = null,
        [Description("Optional new phone number.")] string? phone = null,
        CancellationToken cancellationToken = default)
    {
        var user = new JsonObject();
        if (!string.IsNullOrWhiteSpace(name)) user["name"] = name;
        if (!string.IsNullOrWhiteSpace(email)) user["email"] = email;
        if (!string.IsNullOrWhiteSpace(role)) user["role"] = role;
        if (!string.IsNullOrWhiteSpace(phone)) user["phone"] = phone;
        var body = new JsonObject { ["user"] = user };
        return client.SendAsync(HttpMethod.Put, $"users/{userId}.json", jsonBody: body.ToJsonString(), cancellationToken: cancellationToken);
    }
}
