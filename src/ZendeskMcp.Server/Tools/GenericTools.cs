using System.ComponentModel;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Tools;

[McpServerToolType]
public static class GenericTools
{
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE",
    };

    [McpServerTool(Name = "zendesk_request"), Description(
        "Escape hatch for any Zendesk REST API v2 endpoint not covered by a dedicated tool. " +
        "Use the curated tools where they exist; use this for the long tail (tags, ticket fields, " +
        "automations, triggers, satisfaction ratings, Talk, etc.). " +
        "Example: method='GET', path='/api/v2/ticket_fields.json'.")]
    public static Task<string> ZendeskRequest(
        ZendeskClient client,
        [Description("The relative API path, e.g. '/api/v2/ticket_fields.json' or 'tickets/123.json'. May include a query string.")] string path,
        [Description("HTTP method: GET, POST, PUT, PATCH or DELETE. Default GET.")] string method = "GET",
        [Description("Optional request body as a JSON string (for POST/PUT/PATCH).")] string? body = null,
        CancellationToken cancellationToken = default)
    {
        if (!AllowedMethods.Contains(method))
            throw new ArgumentException($"Unsupported method '{method}'. Allowed: {string.Join(", ", AllowedMethods)}.");

        var normalisedPath = NormalisePath(path);
        var httpMethod = new HttpMethod(method.ToUpperInvariant());
        return client.SendAsync(httpMethod, normalisedPath, jsonBody: body, cancellationToken: cancellationToken);
    }

    private static string NormalisePath(string path)
    {
        var p = path.Trim();
        p = p.TrimStart('/');
        if (p.StartsWith("api/v2/", StringComparison.OrdinalIgnoreCase))
            p = p["api/v2/".Length..];
        return p;
    }
}
