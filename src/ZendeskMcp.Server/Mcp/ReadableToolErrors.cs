using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Mcp;

/// <summary>
/// Surfaces tool-call exceptions to the client as readable error text.
///
/// By default the MCP SDK catches any exception a tool throws and returns a
/// generic "An error occurred invoking '{tool}'." result, logging the real
/// exception only to the server's stderr. That is opaque to callers (including
/// external ones): a mistyped argument name and a Zendesk 404 look identical,
/// which makes the tools appear broken when the request was simply malformed.
///
/// This call-tool filter catches the exception before the SDK flattens it and
/// returns its message instead. Both the argument binder and
/// <see cref="ZendeskApiException"/> already write descriptive messages; the
/// only missing step was letting them reach the client.
/// </summary>
public static class ReadableToolErrors
{
    public static void UseReadableToolErrors(this McpServerOptions options)
    {
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // A cancelled or timed-out request is not a tool error; let it propagate.
                throw;
            }
            catch (Exception ex)
            {
                return ToErrorResult(request.Params?.Name, ex);
            }
        });
    }

    /// <summary>
    /// Maps a tool exception to a client-visible error result. The argument binder
    /// and <see cref="ZendeskApiException"/> already write descriptive messages, so
    /// for those we pass the message through; anything else is wrapped with the tool
    /// name for context.
    /// </summary>
    internal static CallToolResult ToErrorResult(string? toolName, Exception ex)
    {
        var name = string.IsNullOrEmpty(toolName) ? "the tool" : $"'{toolName}'";
        var text = ex switch
        {
            // Already formatted as "Zendesk API request failed: GET tickets/0.json -> HTTP 404 ...".
            ZendeskApiException zendesk => zendesk.Message,
            // The SDK's binder throws this for a missing/misnamed required argument, e.g.
            // "The arguments dictionary is missing a value for the required parameter 'ticketId'."
            ArgumentException argument => $"Invalid arguments for {name}: {argument.Message}",
            _ => $"Calling {name} failed: {ex.Message}",
        };

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = text }],
        };
    }
}
