using System.Text.Json;
using Microsoft.Extensions.Options;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Auth;

/// <summary>
/// In <see cref="UserLoginMode.Mapped"/> mode, refuses every MCP request from a caller who
/// is not signed in (401) or whose signed-in e-mail is not in the user mappings (403).
/// Runs after authentication, so <see cref="HttpContext.User"/> is the validated token.
/// The health check and the OAuth endpoints stay open, because people need them to sign in.
/// </summary>
public sealed class UserLoginGateMiddleware(
    RequestDelegate next,
    IOptions<ZendeskOptions> zendeskOptions,
    IOptions<OAuthConfig> oauthConfig,
    ILogger<UserLoginGateMiddleware> logger)
{
    // Implementation-defined JSON-RPC server error codes (-32000 to -32099).
    private const int AuthenticationRequiredCode = -32001;
    private const int ForbiddenCode = -32003;

    public async Task InvokeAsync(HttpContext context)
    {
        if (PublicPaths.IsPublic(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            var oauth = oauthConfig.Value;
            context.Response.Headers.WWWAuthenticate = string.IsNullOrEmpty(oauth.ExternalBaseUrl)
                ? "Bearer"
                : $"Bearer resource_metadata=\"{oauth.ExternalBaseUrl}/{oauth.RoutePrefix}/.well-known/oauth-protected-resource\"";
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, AuthenticationRequiredCode,
                "Authentication required. Sign in to use this server.");
            return;
        }

        var signInEmail = SignedInUser.GetEmail(context.User);
        if (signInEmail is null)
        {
            logger.LogWarning("Refused a request: the sign-in token has no e-mail address claim.");
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, ForbiddenCode,
                "Your sign-in token does not include an e-mail address, so this server cannot tell who you are.");
            return;
        }

        if (!zendeskOptions.Value.UserMappings.TryGetZendeskEmail(signInEmail, out _))
        {
            logger.LogWarning("Refused a request from {SignInEmail}: not in the user mappings.", signInEmail);
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, ForbiddenCode,
                $"{signInEmail} is not allowed to use this server. Ask an administrator to add you to the user mappings.");
            return;
        }

        await next(context);
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, int code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = (object?)null,
            error = new { code, message },
        });
        return context.Response.WriteAsync(body);
    }
}
