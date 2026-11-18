namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Connection settings for the Zendesk account.
/// Bound from the "Zendesk" configuration section (see Program.cs for the
/// ZENDESK_SUBDOMAIN / ZENDESK_EMAIL / ZENDESK_API_TOKEN / ZENDESK_USER_LOGIN_MODE /
/// ZENDESK_USER_MAPPINGS environment fallbacks).
/// </summary>
public sealed class ZendeskOptions
{
    public const string SectionName = "Zendesk";

    /// <summary>The account subdomain, e.g. "acme" for acme.zendesk.com.</summary>
    public string Subdomain { get; set; } = "";

    /// <summary>
    /// The agent e-mail address that actions are attributed to in <see cref="UserLoginMode.Shared"/>
    /// mode. Not used in <see cref="UserLoginMode.Mapped"/> mode.
    /// </summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// The account API token (admin-generated). A Zendesk API token works with the e-mail of
    /// any agent in the account, which is what lets <see cref="UserLoginMode.Mapped"/> mode
    /// send each user's requests as a different agent.
    /// </summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Whether everyone shares <see cref="Email"/>, or each signed-in user is mapped.</summary>
    public UserLoginMode UserLoginMode { get; set; } = UserLoginMode.Shared;

    /// <summary>Signed-in e-mail → Zendesk agent e-mail. Used only in <see cref="UserLoginMode.Mapped"/> mode.</summary>
    public UserMappings UserMappings { get; set; } = UserMappings.Empty;

    /// <summary>The Zendesk REST API v2 base URL for this account.</summary>
    public string BaseUrl => $"https://{Subdomain}.zendesk.com/api/v2";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Subdomain)
        && !string.IsNullOrWhiteSpace(ApiToken)
        && (UserLoginMode == UserLoginMode.Mapped
            ? UserMappings.Count > 0
            : !string.IsNullOrWhiteSpace(Email));
}
