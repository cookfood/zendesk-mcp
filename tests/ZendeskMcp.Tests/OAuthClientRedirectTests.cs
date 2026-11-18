using ZendeskMcp.Server.Auth;

namespace ZendeskMcp.Tests;

public class OAuthClientRedirectTests
{
    private static AuthSession Session(string redirectUri) => new()
    {
        ClientRedirectUri = redirectUri,
        ClientState = "state 1",
        ClientCodeChallenge = "challenge",
        ClientCodeChallengeMethod = "S256",
        InternalCodeVerifier = "verifier",
    };

    [Fact]
    public void ClientRedirect_AddsCodeAndState()
    {
        var url = OAuthEndpoints.ClientRedirect(Session("https://client.example.com/callback"), ("code", "abc"));

        Assert.Equal("https://client.example.com/callback?code=abc&state=state+1", url);
    }

    [Fact]
    public void ClientRedirect_AddsErrorAndState_ToAnExistingQuery()
    {
        var url = OAuthEndpoints.ClientRedirect(
            Session("https://client.example.com/callback?x=1"),
            ("error", "access_denied"),
            ("error_description", "alice@example.com is not allowed"));

        Assert.Equal(
            "https://client.example.com/callback?x=1&error=access_denied&error_description=alice%40example.com+is+not+allowed&state=state+1",
            url);
    }
}
