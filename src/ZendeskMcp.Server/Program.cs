using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using ZendeskMcp.Server.Auth;
using ZendeskMcp.Server.Mcp;
using ZendeskMcp.Server.Resources;
using ZendeskMcp.Server.Zendesk;

const string serverName = "Zendesk";
const string serverVersion = "1.0.0";
const string serverInstructionsTemplate = """
    You are connected to a Zendesk MCP server. It exposes the Zendesk support
    desk: tickets, comments, users, organisations, groups, views, macros and the
    Help Centre knowledge base.

    Guidance:
    - Use search_tickets with the Zendesk query language to filter, e.g.
      'group:"Dev Team" status:open' or 'assignee:none status:new'.
    - Use list_views / get_view_tickets to read the saved filters from the agent UI,
      and list_groups to resolve a group name to its ID.
    - get_ticket and get_ticket_comments give the full context for a ticket.
    - create_ticket, update_ticket and create_ticket_comment WRITE to live Zendesk —
      always confirm with the user before using them.
    - To attach logs, CSVs, SQL output, or a spreadsheet to a reply, pass them in
      create_ticket_comment's attachments (format 'xlsx' turns CSV text into an Excel
      file). The server uploads and attaches them in the same call.
    - To read a file attached to a ticket, pass its content_url from
      get_ticket_comments to get_ticket_attachment. Text files and Excel workbooks
      come back as text (paged with offset); images as images; PDFs and other files
      as base64.
    - For anything not covered by a dedicated tool, use zendesk_request to call any
      Zendesk REST API v2 endpoint directly.
    - {attribution}
    """;

var useHttp = args.Contains("--http")
    || string.Equals(Environment.GetEnvironmentVariable("TRANSPORT"), "http", StringComparison.OrdinalIgnoreCase);

if (useHttp)
    await RunHttpAsync(args);
else
    await RunStdioAsync(args);

return;

// stdio: for local Docker (docker run -i) and local Claude Code. Logs go to stderr
// so they never corrupt the JSON-RPC protocol stream on stdout.
async Task RunStdioAsync(string[] commandArgs)
{
    var builder = Host.CreateApplicationBuilder(commandArgs);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    var userLoginMode = AddZendeskCore(builder.Services, builder.Configuration);
    if (userLoginMode == UserLoginMode.Mapped)
    {
        throw new InvalidOperationException(
            "UserLoginMode 'Mapped' needs the HTTP transport with Microsoft Entra ID sign-in, so the server " +
            "knows who is calling. The stdio transport has no signed-in user: use 'Shared' instead.");
    }

    builder.Services
        .AddMcpServer(o =>
        {
            o.ServerInfo = new() { Name = serverName, Version = serverVersion };
            o.ServerInstructions = ServerInstructions(userLoginMode);
            o.UseReadableToolErrors();
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithPromptsFromAssembly()
        .WithResourcesFromAssembly();

    var host = builder.Build();
    WarnIfUnconfigured(host.Services);
    await host.RunAsync();
}

// http: for hosting behind APIM (Claude web) and remote Claude Code.
async Task RunHttpAsync(string[] commandArgs)
{
    var builder = WebApplication.CreateBuilder(commandArgs);

    var userLoginMode = AddZendeskCore(builder.Services, builder.Configuration);
    builder.Services.AddHttpClient();

    // Mapped mode picks the Zendesk agent from the signed-in user, so it needs Entra sign-in.
    // The API key used outside Production says nothing about who is calling.
    if (userLoginMode == UserLoginMode.Mapped)
    {
        if (!builder.Environment.IsProduction())
        {
            throw new InvalidOperationException(
                "UserLoginMode 'Mapped' needs Microsoft Entra ID sign-in, which is on only when " +
                "ASPNETCORE_ENVIRONMENT is Production. With the API key there is no signed-in user to map.");
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IZendeskAgentContext, SignedInAgentContext>();
    }

    // Production validates Entra ID JWTs; other environments use the API key middleware.
    if (builder.Environment.IsProduction())
    {
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

        // Accept the API's client ID and its App ID URI as the audience, and log refused tokens.
        var clientId = builder.Configuration["AzureAd:ClientId"];
        var audience = builder.Configuration["AzureAd:Audience"];
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure<ILoggerFactory>((options, loggers) => EntraTokenValidation.Apply(
                options, clientId, audience, loggers.CreateLogger(typeof(EntraTokenValidation).FullName!)));
    }
    else
    {
        builder.Services.AddAuthentication();
    }
    builder.Services.AddAuthorization();

    // OAuth 2.1 bridge to Entra ID (so Claude web can connect).
    builder.Services.Configure<OAuthConfig>(builder.Configuration.GetSection("OAuth"));
    builder.Services.AddSingleton<AuthorizationStore>();
    builder.Services.AddHostedService<AuthorizationStoreCleanup>();
    builder.Services.AddSingleton(new Azure.Identity.ManagedIdentityCredential());

    builder.Services.AddHealthChecks();

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.User?.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    PermitLimit = 60,
                    QueueLimit = 0,
                }));
    });

    builder.Services
        .AddMcpServer(o =>
        {
            o.ServerInfo = new() { Name = serverName, Version = serverVersion };
            o.ServerInstructions = ServerInstructions(userLoginMode);
            o.UseReadableToolErrors();
        })
        // Stateless: no MCP session state is held in process memory. Every tool here is a
        // plain request/response call against the Zendesk REST API using credentials from
        // configuration, so there is nothing to keep between requests. (In Mapped mode the
        // Zendesk agent is worked out again from each request's own token.)
        // Stateful mode expires a session after IdleTimeout (default 2 hours) and loses every
        // session on restart or scale-out. A client that then replays its old Mcp-Session-Id
        // gets a 404 "Session not found" and cannot recover without re-adding the connector.
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly()
        .WithPromptsFromAssembly()
        .WithResourcesFromAssembly();

    var app = builder.Build();

    // Correlation ID for tracing.
    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N")[..12];
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await next();
    });

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    if (!app.Environment.IsProduction())
    {
        app.UseMiddleware<ApiKeyAuthMiddleware>();
    }

    // Refuses MCP requests from anyone who is not in the user mappings.
    if (userLoginMode == UserLoginMode.Mapped)
    {
        app.UseMiddleware<UserLoginGateMiddleware>();
    }

    app.MapOAuthEndpoints();
    app.MapMcp();
    app.MapHealthChecks("/healthz").AllowAnonymous();

    WarnIfUnconfigured(app.Services);
    await app.RunAsync();
}

static string ServerInstructions(UserLoginMode userLoginMode) =>
    serverInstructionsTemplate.Replace("{attribution}", userLoginMode == UserLoginMode.Mapped
        ? "Actions are attributed to the Zendesk agent mapped to the signed-in user."
        : "All actions are attributed to the shared service account.");

// Registers the Zendesk client and its options. Returns the user login mode, which is read
// and checked here, up front, so a bad value or mapping stops the server at startup rather
// than on the first request.
static UserLoginMode AddZendeskCore(IServiceCollection services, IConfiguration config)
{
    var userLoginMode = UserLoginModes.Parse(First(config, "Zendesk:UserLoginMode", "ZENDESK_USER_LOGIN_MODE"));
    var userMappings = UserMappings.Empty;
    if (userLoginMode == UserLoginMode.Mapped)
    {
        userMappings = UserMappings.Parse(UserMappingValues(config));
        if (userMappings.Count == 0)
        {
            throw new InvalidOperationException(
                "UserLoginMode is 'Mapped' but no user mappings are set, so nobody could use the server. " +
                "Set ZENDESK_USER_MAPPINGS (or Zendesk:UserMappings), e.g. 'person@example.com=agent@example.com'.");
        }
    }

    services.AddOptions<ZendeskOptions>().Configure(o =>
    {
        // Trim values: an .env with Windows (CRLF) line endings passed via
        // `docker --env-file` leaks a trailing '\r' into each value.
        o.Subdomain = First(config, "Zendesk:Subdomain", "ZENDESK_SUBDOMAIN");
        o.Email = First(config, "Zendesk:Email", "ZENDESK_EMAIL");
        o.ApiToken = First(config, "Zendesk:ApiToken", "ZENDESK_API_TOKEN", "ZENDESK_API_KEY");
        o.UserLoginMode = userLoginMode;
        o.UserMappings = userMappings;
    });

    services.AddHttpClient(nameof(ZendeskClient), c => c.Timeout = TimeSpan.FromSeconds(60));
    services.AddHttpClient($"{nameof(ZendeskClient)}.NoRedirect")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

    services.AddSingleton<ZendeskClient>();
    services.AddSingleton<KnowledgeBaseService>();

    return userLoginMode;
}

// The user mappings, from either Zendesk:UserMappings or ZENDESK_USER_MAPPINGS (first one set
// wins, as with the other settings). Zendesk:UserMappings may be a single string or, in
// appsettings.json, a list of strings; each string can hold several entries.
static IEnumerable<string?> UserMappingValues(IConfiguration config)
{
    var section = config.GetSection("Zendesk:UserMappings");
    string?[] values = [section.Value, .. section.GetChildren().Select(child => child.Value)];
    return values.Any(v => !string.IsNullOrWhiteSpace(v)) ? values : [config["ZENDESK_USER_MAPPINGS"]];
}

static string First(IConfiguration config, params string[] keys)
{
    foreach (var key in keys)
    {
        var value = config[key];
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
    }
    return "";
}

static void WarnIfUnconfigured(IServiceProvider services)
{
    var options = services.GetRequiredService<IOptions<ZendeskOptions>>().Value;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    if (options.UserLoginMode == UserLoginMode.Mapped)
    {
        logger.LogInformation(
            "User login mode is Mapped: {Count} signed-in users can use the server, each as their mapped Zendesk agent.",
            options.UserMappings.Count);
        if (!string.IsNullOrWhiteSpace(options.Email))
            logger.LogWarning("ZENDESK_EMAIL is set but not used, because the user login mode is Mapped.");
    }
    else if (UserMappingValues(services.GetRequiredService<IConfiguration>()).Any(v => !string.IsNullOrWhiteSpace(v)))
    {
        logger.LogWarning(
            "User mappings are set but not used, because the user login mode is Shared: everyone who can reach " +
            "the server acts as ZENDESK_EMAIL. Set ZENDESK_USER_LOGIN_MODE=Mapped to apply them.");
    }

    if (options.IsConfigured) return;

    logger.LogWarning(options.UserLoginMode == UserLoginMode.Mapped
        ? "Zendesk credentials are not fully configured. Set ZENDESK_SUBDOMAIN and ZENDESK_API_TOKEN (or the Zendesk:* settings)."
        : "Zendesk credentials are not fully configured. Set ZENDESK_SUBDOMAIN, ZENDESK_EMAIL and ZENDESK_API_TOKEN (or the Zendesk:* settings).");
}

// Exposed for the test project's WebApplicationFactory<Program>.
public partial class Program { }
