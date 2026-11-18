using Microsoft.Extensions.Options;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Auth;

/// <summary>
/// Resolves the Zendesk agent for the current HTTP request by looking up the signed-in
/// user's e-mail in the configured user mappings. Used in <see cref="UserLoginMode.Mapped"/>
/// mode. The HTTP transport runs tool calls on the request's own execution context, so
/// <see cref="IHttpContextAccessor"/> sees the caller of that tool call.
/// </summary>
public sealed class SignedInAgentContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<ZendeskOptions> options) : IZendeskAgentContext
{
    public string? AgentEmail
    {
        get
        {
            var signInEmail = SignedInUser.GetEmail(httpContextAccessor.HttpContext?.User);
            return options.Value.UserMappings.TryGetZendeskEmail(signInEmail, out var agentEmail)
                ? agentEmail
                : null;
        }
    }
}
