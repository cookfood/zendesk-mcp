using System.Net;

namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Thrown when the Zendesk API returns a non-success status code. The message is
/// deliberately descriptive (status, reason and response body) so it surfaces
/// usefully to the model when a tool call fails.
/// </summary>
public sealed class ZendeskApiException(HttpStatusCode statusCode, string method, string path, string responseBody)
    : Exception($"Zendesk API request failed: {method} {path} -> HTTP {(int)statusCode} {statusCode}. {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
