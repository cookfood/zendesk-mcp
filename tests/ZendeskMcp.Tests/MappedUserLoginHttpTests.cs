using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

/// <summary>
/// Runs the real HTTP host in Mapped mode, with a stand-in for Entra sign-in and a fake
/// Zendesk, to check that a tool call reaches Zendesk as the caller's mapped agent and that
/// anyone outside the mapping is refused before any tool runs.
/// </summary>
public class MappedUserLoginHttpTests : IClassFixture<MappedUserLoginHttpTests.MappedHostFactory>
{
    private readonly MappedHostFactory _factory;

    public MappedUserLoginHttpTests(MappedHostFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("alice@example.com", "team@example.com")]
    [InlineData("bob@example.com", "team@example.com")]
    [InlineData("carol@example.com", "carol@example.com")]
    public async Task ToolCall_ReachesZendeskAsTheMappedAgent(string signInEmail, string expectedAgent)
    {
        using var response = await CallGetCurrentUserAsync(signInEmail);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"id\":1", body);
        Assert.DoesNotContain("\"isError\":true", body);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{expectedAgent}/token:secret-token"));
        Assert.Contains(expected, _factory.Zendesk.AuthHeadersSeen);
    }

    [Fact]
    public async Task UnmappedUser_IsRefused_AndZendeskIsNeverCalled()
    {
        var before = _factory.Zendesk.AuthHeadersSeen.Count;

        using var response = await CallGetCurrentUserAsync("mallory@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("mallory@example.com is not allowed", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, _factory.Zendesk.AuthHeadersSeen.Count);
    }

    [Fact]
    public async Task AnonymousCaller_IsRefusedWith401()
    {
        using var response = await CallGetCurrentUserAsync(signInEmail: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthCheck_StaysOpen()
    {
        using var response = await _factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> CallGetCurrentUserAsync(string? signInEmail)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_current_user","arguments":{}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (signInEmail is not null) request.Headers.Add(TestSignIn.Header, signInEmail);

        return await client.SendAsync(request);
    }

    public sealed class MappedHostFactory : WebApplicationFactory<Program>
    {
        public FakeZendesk Zendesk { get; } = new();

        public MappedHostFactory()
        {
            // Program picks its transport from this variable before any configuration is read.
            Environment.SetEnvironmentVariable("TRANSPORT", "http");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            // UseSetting reaches Program as command-line configuration, so it is visible when
            // Program reads the user login mode while registering services.
            builder.UseSetting("Zendesk:Subdomain", "acme");
            builder.UseSetting("Zendesk:ApiToken", "secret-token");
            builder.UseSetting("Zendesk:UserLoginMode", "Mapped");
            builder.UseSetting("Zendesk:UserMappings",
                "alice@example.com=team@example.com; bob@example.com=team@example.com; carol@example.com");
            builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000001");
            builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000002");

            builder.ConfigureLogging(logging => logging.ClearProviders());

            builder.ConfigureTestServices(services =>
            {
                // Stand in for Entra: the test names the signed-in user in a header.
                services.AddAuthentication(TestSignIn.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestSignIn>(TestSignIn.SchemeName, _ => { });

                services.AddHttpClient(nameof(ZendeskClient))
                    .ConfigurePrimaryHttpMessageHandler(() => Zendesk);
            });
        }
    }

    public sealed class FakeZendesk : HttpMessageHandler
    {
        public List<string> AuthHeadersSeen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (AuthHeadersSeen) AuthHeadersSeen.Add(request.Headers.Authorization?.Parameter ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"user\":{\"id\":1}}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TestSignIn(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string Header = "X-Test-Signed-In-As";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var email = Request.Headers[Header].FirstOrDefault();
            if (string.IsNullOrEmpty(email)) return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim("preferred_username", email)], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
