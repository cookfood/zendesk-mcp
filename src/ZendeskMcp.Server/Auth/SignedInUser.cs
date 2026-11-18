using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ZendeskMcp.Server.Auth;

/// <summary>
/// Reads the e-mail address a person signed in with from their Microsoft Entra ID token.
/// </summary>
public static class SignedInUser
{
    // Checked in this order. Entra v2.0 access tokens carry preferred_username; v1.0 tokens
    // carry upn and unique_name; email is present only when it is added as an optional claim.
    // The ClaimTypes.* forms cover ASP.NET Core's inbound claim-type mapping. For a member of
    // the tenant each of these is normally the user principal name (the sign-in address).
    private static readonly string[] EmailClaimTypes =
    [
        "preferred_username",
        "upn",
        ClaimTypes.Upn,
        "email",
        ClaimTypes.Email,
        "unique_name",
        ClaimTypes.Name,
    ];

    /// <summary>
    /// The signed-in e-mail of an authenticated user, or null when the user is not
    /// authenticated or the token has no e-mail-like claim.
    /// </summary>
    public static string? GetEmail(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true ? GetEmail(user.Claims) : null;

    /// <summary>The first e-mail-like value among the known claim types, or null.</summary>
    public static string? GetEmail(IEnumerable<Claim> claims)
    {
        var list = claims as IReadOnlyCollection<Claim> ?? claims.ToList();
        foreach (var type in EmailClaimTypes)
        {
            var value = list.FirstOrDefault(c => c.Type == type)?.Value.Trim();
            if (!string.IsNullOrEmpty(value) && value.Contains('@')) return value;
        }
        return null;
    }

    /// <summary>
    /// Reads the signed-in e-mail from the access token in an Entra token-endpoint response.
    /// The token is not validated here: it has just come straight from Entra over TLS, and
    /// every later request is validated by the JWT bearer handler. Returns null when the
    /// response or token cannot be read.
    /// </summary>
    public static string? GetEmailFromTokenResponse(string tokenResponseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(tokenResponseJson);
            if (!document.RootElement.TryGetProperty("access_token", out var accessToken)
                || accessToken.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var token = new JsonWebToken(accessToken.GetString()!);
            return GetEmail(token.Claims);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or SecurityTokenException)
        {
            return null;
        }
    }
}
