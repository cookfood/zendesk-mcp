using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using ZendeskMcp.Server.Auth;
using ZendeskMcp.Server.Resources;
using ZendeskMcp.Server.Zendesk;

const string serverName = "Zendesk";
const string serverVersion = "1.0.0";
const string serverInstructions = """
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
    - For anything not covered by a dedicated tool, use zendesk_request to call any
      Zendesk REST API v2 endpoint directly.
    - All actions are attributed to the shared service account.
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

    AddZendeskCore(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer(o =>
        {
            o.ServerInfo = new() { Name = serverName, Version = serverVersion };
            o.ServerInstructions = serverInstructions;
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

    AddZendeskCore(builder.Services, builder.Configuration);
    builder.Services.AddHttpClient();

    // Production validates Entra ID JWTs; other environments use the API key middleware.
    if (builder.Environment.IsProduction())
    {
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
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
            o.ServerInstructions = serverInstructions;
        })
        // Stateless: no MCP session state is held in process memory. Every tool here is a
        // plain request/response call against the Zendesk REST API using shared service-account
        // credentials from configuration, so there is nothing to keep between requests.
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

    app.MapOAuthEndpoints();
    app.MapMcp();
    app.MapHealthChecks("/healthz").AllowAnonymous();

    WarnIfUnconfigured(app.Services);
    await app.RunAsync();
}

static void AddZendeskCore(IServiceCollection services, IConfiguration config)
{
    services.AddOptions<ZendeskOptions>().Configure(o =>
    {
        // Trim values: an .env with Windows (CRLF) line endings passed via
        // `docker --env-file` leaks a trailing '\r' into each value.
        o.Subdomain = First(config, "Zendesk:Subdomain", "ZENDESK_SUBDOMAIN");
        o.Email = First(config, "Zendesk:Email", "ZENDESK_EMAIL");
        o.ApiToken = First(config, "Zendesk:ApiToken", "ZENDESK_API_TOKEN", "ZENDESK_API_KEY");
    });

    services.AddHttpClient(nameof(ZendeskClient), c => c.Timeout = TimeSpan.FromSeconds(60));
    services.AddHttpClient($"{nameof(ZendeskClient)}.NoRedirect")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

    services.AddSingleton<ZendeskClient>();
    services.AddSingleton<KnowledgeBaseService>();
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
    if (options.IsConfigured) return;

    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    logger.LogWarning(
        "Zendesk credentials are not fully configured. Set ZENDESK_SUBDOMAIN, ZENDESK_EMAIL and ZENDESK_API_TOKEN (or the Zendesk:* settings).");
}

// Exposed for the test project's WebApplicationFactory<Program>.
public partial class Program { }
