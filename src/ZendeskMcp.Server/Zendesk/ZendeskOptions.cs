namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Connection settings for the shared Zendesk service account.
/// Bound from the "Zendesk" configuration section (see Program.cs for the
/// ZENDESK_SUBDOMAIN / ZENDESK_EMAIL / ZENDESK_API_TOKEN environment fallbacks).
/// </summary>
public sealed class ZendeskOptions
{
    public const string SectionName = "Zendesk";

    /// <summary>The account subdomain, e.g. "acme" for acme.zendesk.com.</summary>
    public string Subdomain { get; set; } = "";

    /// <summary>The agent e-mail address that actions are attributed to.</summary>
    public string Email { get; set; } = "";

    /// <summary>The account API token (admin-generated).</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>The Zendesk REST API v2 base URL for this account.</summary>
    public string BaseUrl => $"https://{Subdomain}.zendesk.com/api/v2";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Subdomain)
        && !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(ApiToken);
}
