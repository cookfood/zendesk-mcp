using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Server.Prompts;

[McpServerPromptType]
public static class TicketPrompts
{
    [McpServerPrompt(Name = "analyze-ticket"), Description("Analyse a Zendesk ticket and summarise the issue, status and key interactions.")]
    public static string AnalyzeTicket(
        [Description("The ID of the ticket to analyse.")] long ticketId)
        => $"""
            Please analyse Zendesk ticket {ticketId}.

            1. Use get_ticket to fetch the ticket, and get_ticket_comments to fetch the conversation.
            2. Then provide:
               - A concise summary of the issue.
               - The current status and a short timeline of what has happened.
               - The key interaction points and anything still outstanding.
            """;

    [McpServerPrompt(Name = "draft-ticket-response"), Description("Draft a professional response to a Zendesk ticket.")]
    public static string DraftTicketResponse(
        [Description("The ID of the ticket to respond to.")] long ticketId)
        => $"""
            Please draft a response to Zendesk ticket {ticketId}.

            1. Use get_ticket and get_ticket_comments to understand the full context. If relevant, consult the
               zendesk://knowledge-base resource or search_articles for accurate information.
            2. Draft a reply that:
               - Acknowledges the customer's concern.
               - Addresses the specific issues raised.
               - Provides clear next steps.
               - Keeps a warm, professional tone.
            3. Show me the draft and ask for confirmation before posting it with create_ticket_comment.
            """;
}
