namespace ZendeskMcp.Server.Auth;

/// <summary>
/// Paths that must work before the caller has signed in: the health check and the
/// OAuth discovery and flow endpoints. Every other path is the MCP endpoint itself.
/// </summary>
internal static class PublicPaths
{
    public static bool IsPublic(PathString path) =>
        path.StartsWithSegments("/healthz")
        || path.StartsWithSegments("/.well-known")
        || path.StartsWithSegments("/authorize")
        || path.StartsWithSegments("/oauth-callback")
        || path.StartsWithSegments("/token")
        || path.StartsWithSegments("/register");
}
