namespace ZendeskMcp.Server.Auth;

public class OAuthConfig
{
    public string ClientId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Scopes { get; set; } = "";
    public string ExternalBaseUrl { get; set; } = "";

    public string EntraAuthorizeUrl => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize";
    public string EntraTokenUrl => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";
}
