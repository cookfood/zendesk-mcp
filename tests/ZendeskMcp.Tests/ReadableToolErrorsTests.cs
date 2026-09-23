using System.Net;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZendeskMcp.Server.Mcp;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class ReadableToolErrorsTests
{
    private static string TextOf(CallToolResult result)
    {
        Assert.True(result.IsError);
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return block.Text;
    }

    [Fact]
    public void ToErrorResult_PassesThroughZendeskApiMessage()
    {
        var ex = new ZendeskApiException(HttpStatusCode.NotFound, "GET", "tickets/0.json", "{\"error\":\"RecordNotFound\"}");

        var text = TextOf(ReadableToolErrors.ToErrorResult("get_ticket", ex));

        // The Zendesk message is already descriptive, so it is surfaced verbatim.
        Assert.Equal(ex.Message, text);
        Assert.Contains("HTTP 404", text);
        Assert.Contains("RecordNotFound", text);
    }

    [Fact]
    public void ToErrorResult_NamesTheMissingArgument()
    {
        // Mirrors the binder's message for a missing/misnamed required argument.
        var ex = new ArgumentException(
            "The arguments dictionary is missing a value for the required parameter 'ticketId'.", "arguments");

        var text = TextOf(ReadableToolErrors.ToErrorResult("get_ticket", ex));

        Assert.Contains("Invalid arguments for 'get_ticket'", text);
        Assert.Contains("required parameter 'ticketId'", text);
    }

    [Fact]
    public void ToErrorResult_WrapsUnexpectedExceptionWithToolName()
    {
        var text = TextOf(ReadableToolErrors.ToErrorResult("get_ticket", new InvalidOperationException("boom")));

        Assert.Contains("Calling 'get_ticket' failed", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void ToErrorResult_FallsBackWhenToolNameMissing()
    {
        var text = TextOf(ReadableToolErrors.ToErrorResult(null, new InvalidOperationException("boom")));

        Assert.Contains("the tool", text);
    }

    [Fact]
    public void UseReadableToolErrors_RegistersOneCallToolFilter()
    {
        var options = new McpServerOptions();

        options.UseReadableToolErrors();

        Assert.Single(options.Filters.Request.CallToolFilters);
    }
}
