namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Supplies the Zendesk agent e-mail address for the request being handled, when it
/// depends on who is calling (see <see cref="UserLoginMode.Mapped"/>). When no context
/// is registered, <see cref="ZendeskClient"/> uses <see cref="ZendeskOptions.Email"/>.
/// </summary>
public interface IZendeskAgentContext
{
    /// <summary>
    /// The agent e-mail to send the current request as, or null when the caller is not
    /// allowed to use the server.
    /// </summary>
    string? AgentEmail { get; }
}
