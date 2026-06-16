namespace ZendeskMcp.Server.Auth;

/// <summary>
/// Simple shared-secret gate for non-production HTTP hosting (and remote Claude
/// Code use). Reads the expected key from configuration ("Mcp:ApiKey") and checks
/// the X-API-Key header. Health and OAuth discovery paths are exempt. In
/// production this is replaced by Entra ID JWT validation at the APIM layer.
/// </summary>
public class ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly string? _apiKey = configuration["Mcp:ApiKey"] ?? configuration["MCP_API_KEY"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isExempt = path.StartsWithSegments("/healthz")
            || path.StartsWithSegments("/.well-known")
            || path.StartsWithSegments("/authorize")
            || path.StartsWithSegments("/oauth-callback")
            || path.StartsWithSegments("/token")
            || path.StartsWithSegments("/register");

        if (string.IsNullOrEmpty(_apiKey) || isExempt)
        {
            await next(context);
            return;
        }

        var providedKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(providedKey) || providedKey != _apiKey)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid or missing API key");
            return;
        }

        await next(context);
    }
}
