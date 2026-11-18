using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ZendeskMcp.Tests;

/// <summary>
/// Checks the audience rules of the app's own Entra token validation, as configured in
/// Production. Entra issues v1 access tokens unless the app registration asks for v2, and a
/// v1 token's audience is whichever form the client asked for: the App ID URI
/// (api://&lt;client id&gt;) or the bare client ID. Both must be accepted.
/// </summary>
public class EntraTokenAudienceTests : IClassFixture<EntraTokenAudienceTests.ProductionHostFactory>
{
    private const string ClientId = "00000000-0000-0000-0000-0000000000a1";
    private const string AppIdUri = "api://" + ClientId;

    private readonly TokenValidationParameters _parameters;

    public EntraTokenAudienceTests(ProductionHostFactory factory)
    {
        _parameters = factory.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters;
    }

    [Theory]
    [InlineData("1.0", ClientId)]
    [InlineData("1.0", AppIdUri)]
    [InlineData("2.0", ClientId)]
    public void TokenForThisApi_IsAccepted(string version, string audience)
    {
        var token = UnsignedToken(new { aud = audience, ver = version });

        var error = Record.Exception(() => Validators.ValidateAudience([audience], token, _parameters));

        Assert.Null(error);
    }

    [Theory]
    [InlineData("1.0", "00000000-0000-0000-0000-0000000000b2")]
    [InlineData("1.0", "api://00000000-0000-0000-0000-0000000000b2")]
    [InlineData("2.0", "https://graph.microsoft.com")]
    public void TokenForAnotherApi_IsRejected(string version, string audience)
    {
        var token = UnsignedToken(new { aud = audience, ver = version });

        Assert.ThrowsAny<SecurityTokenInvalidAudienceException>(
            () => Validators.ValidateAudience([audience], token, _parameters));
    }

    private static JsonWebToken UnsignedToken(object payload)
    {
        static string Encode(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return new JsonWebToken($"{Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{Encode(JsonSerializer.Serialize(payload))}.");
    }

    public sealed class ProductionHostFactory : WebApplicationFactory<Program>
    {
        public ProductionHostFactory()
        {
            // Program picks its transport from this variable before any configuration is read.
            Environment.SetEnvironmentVariable("TRANSPORT", "http");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            builder.UseSetting("Zendesk:Subdomain", "acme");
            builder.UseSetting("Zendesk:Email", "agent@example.com");
            builder.UseSetting("Zendesk:ApiToken", "secret-token");

            // The shape of a typical deployment: the audience set to the App ID URI.
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000001");
            builder.UseSetting("AzureAd:ClientId", ClientId);
            builder.UseSetting("AzureAd:Audience", AppIdUri);

            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
    }
}
