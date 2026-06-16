namespace ZendeskMcp.Server.Auth;

public class OAuthConfig
{
    public string ClientId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Scopes { get; set; } = "";
    public string ExternalBaseUrl { get; set; } = "";

    /// <summary>
    /// The public path segment this server is exposed under (the APIM API suffix), without slashes.
    /// The OAuth metadata advertises URLs as {ExternalBaseUrl}/{RoutePrefix}/... Default "mcp".
    /// </summary>
    public string RoutePrefix { get; set; } = "mcp";

    public string EntraAuthorizeUrl => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize";
    public string EntraTokenUrl => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";
}
