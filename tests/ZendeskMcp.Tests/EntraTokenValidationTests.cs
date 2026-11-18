using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ZendeskMcp.Server.Auth;

namespace ZendeskMcp.Tests;

public class EntraTokenValidationTests
{
    private const string ClientId = "00000000-0000-0000-0000-0000000000a1";

    [Fact]
    public void ValidAudiences_IncludesBothFormsOfTheClientId_AndTheConfiguredAudience()
    {
        var audiences = EntraTokenValidation.ValidAudiences(ClientId, "https://api.example.com");

        Assert.Equal(["https://api.example.com", ClientId, $"api://{ClientId}"], audiences);
    }

    [Fact]
    public void ValidAudiences_DoesNotRepeatAnAudienceThatIsAlreadyTheAppIdUri()
    {
        var audiences = EntraTokenValidation.ValidAudiences(ClientId, $"api://{ClientId}");

        Assert.Equal([$"api://{ClientId}", ClientId], audiences);
    }

    [Fact]
    public void ValidAudiences_IsEmpty_WithoutAClientIdOrAudience()
    {
        Assert.Empty(EntraTokenValidation.ValidAudiences(null, " "));
    }

    [Fact]
    public void Apply_WithoutAClientId_LeavesTheDefaultAudienceCheckAlone()
    {
        var options = new JwtBearerOptions();
        AudienceValidator existing = (_, _, _) => true;
        options.TokenValidationParameters.AudienceValidator = existing;

        EntraTokenValidation.Apply(options, null, null, new ListLogger());

        Assert.Same(existing, options.TokenValidationParameters.AudienceValidator);
    }

    [Fact]
    public async Task RefusedToken_IsLogged_WithTheAudience_AndEarlierHandlersStillRun()
    {
        var options = new JwtBearerOptions();
        var earlierHandlerRan = false;
        options.Events.OnAuthenticationFailed = _ =>
        {
            earlierHandlerRan = true;
            return Task.CompletedTask;
        };
        var logger = new ListLogger();
        EntraTokenValidation.Apply(options, ClientId, null, logger);

        var context = new AuthenticationFailedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)),
            options)
        {
            Exception = new SecurityTokenInvalidAudienceException("IDX10214: Audience validation failed.")
            {
                InvalidAudience = "api://some-other-api",
            },
        };
        await options.Events.AuthenticationFailed(context);

        Assert.True(earlierHandlerRan);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("SecurityTokenInvalidAudienceException", entry.Message);
        Assert.Contains("api://some-other-api", entry.Message);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
