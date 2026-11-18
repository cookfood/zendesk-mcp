using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZendeskMcp.Server.Auth;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class UserLoginGateMiddlewareTests
{
    private static async Task<(HttpContext Context, bool NextCalled)> InvokeAsync(string path, ClaimsPrincipal? user)
    {
        var nextCalled = false;
        var middleware = new UserLoginGateMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(new ZendeskOptions
            {
                UserLoginMode = UserLoginMode.Mapped,
                UserMappings = UserMappings.Parse("alice@example.com=team@example.com"),
            }),
            Options.Create(new OAuthConfig { ExternalBaseUrl = "https://gateway.example.com", RoutePrefix = "zendesk-mcp" }),
            NullLogger<UserLoginGateMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (user is not null) context.User = user;

        await middleware.InvokeAsync(context);
        return (context, nextCalled);
    }

    private static ClaimsPrincipal SignedIn(string email) =>
        new(new ClaimsIdentity([new Claim("preferred_username", email)], authenticationType: "Bearer"));

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task MappedUser_IsLetThrough()
    {
        var (context, nextCalled) = await InvokeAsync("/", SignedIn("ALICE@example.com"));

        Assert.True(nextCalled);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnmappedUser_IsRefusedWith403()
    {
        var (context, nextCalled) = await InvokeAsync("/", SignedIn("mallory@example.com"));

        Assert.False(nextCalled);
        Assert.Equal(403, context.Response.StatusCode);
        using var body = JsonDocument.Parse(Body(context));
        Assert.Contains("mallory@example.com is not allowed", body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SignedInUserWithNoEmailClaim_IsRefusedWith403()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "x")], authenticationType: "Bearer"));

        var (context, nextCalled) = await InvokeAsync("/", user);

        Assert.False(nextCalled);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task AnonymousCaller_GetsA401PointingAtTheResourceMetadata()
    {
        var (context, nextCalled) = await InvokeAsync("/", user: null);

        Assert.False(nextCalled);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(
            "Bearer resource_metadata=\"https://gateway.example.com/zendesk-mcp/.well-known/oauth-protected-resource\"",
            context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/authorize")]
    [InlineData("/oauth-callback")]
    [InlineData("/token")]
    [InlineData("/register")]
    public async Task PublicPaths_StayOpen(string path)
    {
        var (_, nextCalled) = await InvokeAsync(path, user: null);

        Assert.True(nextCalled);
    }
}
