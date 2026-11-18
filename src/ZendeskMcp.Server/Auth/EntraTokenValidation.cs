using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace ZendeskMcp.Server.Auth;

/// <summary>
/// Adjusts the app's own validation of Microsoft Entra ID access tokens (Production only),
/// on top of what Microsoft.Identity.Web sets up.
/// </summary>
internal static class EntraTokenValidation
{
    /// <summary>
    /// The audiences a token for this API can carry. A v1 access token carries whichever form
    /// the client asked for: the App ID URI (<c>api://&lt;client id&gt;</c>) when the scope was
    /// requested as <c>api://&lt;client id&gt;/scope</c>, or the bare client ID when it was
    /// requested as <c>&lt;client id&gt;/scope</c>. A v2 token always carries the client ID.
    /// A configured <c>AzureAd:Audience</c> (for example a custom App ID URI) is kept too.
    /// </summary>
    internal static string[] ValidAudiences(string? clientId, string? audience)
    {
        var audiences = new List<string>();
        if (!string.IsNullOrWhiteSpace(audience)) audiences.Add(audience.Trim());
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            audiences.Add(clientId.Trim());
            audiences.Add($"api://{clientId.Trim()}");
        }
        return audiences.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Accepts every audience form from <see cref="ValidAudiences"/>, and logs why a token was
    /// refused, so a mismatch shows up in the logs instead of only as a 401.
    /// </summary>
    internal static void Apply(JwtBearerOptions options, string? clientId, string? audience, ILogger logger)
    {
        var audiences = ValidAudiences(clientId, audience);
        if (audiences.Length > 0)
        {
            // With AzureAd:Audience set, the default check accepts that one form only, which
            // refuses tokens issued for the bare client ID. Replace it with the full list.
            var parameters = options.TokenValidationParameters;
            parameters.AudienceValidator = null;
            parameters.ValidAudience = null;
            parameters.ValidAudiences = audiences;
        }

        options.Events ??= new JwtBearerEvents();
        var previous = options.Events.OnAuthenticationFailed;
        options.Events.OnAuthenticationFailed = async context =>
        {
            // Audience and issuer values identify the API and the tenant, not the person.
            var detail = context.Exception switch
            {
                SecurityTokenInvalidAudienceException e => $"token audience '{e.InvalidAudience}'",
                SecurityTokenInvalidIssuerException e => $"token issuer '{e.InvalidIssuer}'",
                _ => "",
            };
            logger.LogWarning("Entra access token refused: {Error} {Detail} {Reason}",
                context.Exception.GetType().Name, detail, context.Exception.Message);

            await previous(context);
        };
    }
}
