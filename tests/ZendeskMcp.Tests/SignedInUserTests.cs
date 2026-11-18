using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ZendeskMcp.Server.Auth;

namespace ZendeskMcp.Tests;

public class SignedInUserTests
{
    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Bearer"));

    [Fact]
    public void GetEmail_PrefersPreferredUsername()
    {
        var user = User(("email", "mail@example.com"), ("preferred_username", "alice@example.com"));

        Assert.Equal("alice@example.com", SignedInUser.GetEmail(user));
    }

    [Fact]
    public void GetEmail_ReadsUpn_FromV1Token()
    {
        Assert.Equal("alice@example.com", SignedInUser.GetEmail(User(("upn", "alice@example.com"))));
    }

    [Fact]
    public void GetEmail_ReadsMappedClaimTypes()
    {
        Assert.Equal("alice@example.com", SignedInUser.GetEmail(User((ClaimTypes.Upn, "alice@example.com"))));
        Assert.Equal("bob@example.com", SignedInUser.GetEmail(User((ClaimTypes.Email, "bob@example.com"))));
    }

    [Fact]
    public void GetEmail_SkipsValuesThatAreNotEmailAddresses()
    {
        var user = User((ClaimTypes.Name, "Alice Smith"), ("email", "alice@example.com"));

        Assert.Equal("alice@example.com", SignedInUser.GetEmail(user));
    }

    [Fact]
    public void GetEmail_ReturnsNull_WhenNoEmailClaim()
    {
        Assert.Null(SignedInUser.GetEmail(User(("oid", "00000000-0000-0000-0000-000000000001"))));
    }

    [Fact]
    public void GetEmail_ReturnsNull_WhenNotAuthenticated()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice@example.com")]));

        Assert.Null(SignedInUser.GetEmail(anonymous));
        Assert.Null(SignedInUser.GetEmail((ClaimsPrincipal?)null));
    }

    [Fact]
    public void GetEmailFromTokenResponse_ReadsAccessTokenClaims()
    {
        var json = TokenResponse(new { upn = "alice@example.com", oid = "x" });

        Assert.Equal("alice@example.com", SignedInUser.GetEmailFromTokenResponse(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"not-a-jwt\"}")]
    [InlineData("{\"access_token\":42}")]
    public void GetEmailFromTokenResponse_ReturnsNull_WhenUnreadable(string json)
    {
        Assert.Null(SignedInUser.GetEmailFromTokenResponse(json));
    }

    /// <summary>An Entra-style token response holding an unsigned JWT with the given payload.</summary>
    internal static string TokenResponse(object payload)
    {
        static string Encode(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var jwt = $"{Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{Encode(JsonSerializer.Serialize(payload))}.";
        return JsonSerializer.Serialize(new { access_token = jwt, token_type = "Bearer" });
    }
}
