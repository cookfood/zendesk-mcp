using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Options;

namespace ZendeskMcp.Server.Auth;

/// <summary>
/// OAuth 2.1 bridge so Claude web can connect. The server presents itself as the
/// authorisation server (metadata + dynamic client registration) and bridges the
/// flow to Microsoft Entra ID, keeping two separate PKCE layers: one with the MCP
/// client and an internal one with Entra.
/// </summary>
public static class OAuthEndpoints
{
    public static WebApplication MapOAuthEndpoints(this WebApplication app)
    {
        // RFC 9728: Protected Resource Metadata
        app.MapGet("/.well-known/oauth-protected-resource", (IOptions<OAuthConfig> config) =>
        {
            var c = config.Value;
            return Results.Json(new
            {
                resource = $"{c.ExternalBaseUrl}/{c.RoutePrefix}",
                // Must carry the /{RoutePrefix} path so RFC 8414 discovery resolves to THIS
                // server's metadata (…/zendesk-mcp/.well-known/…) and not the gateway-root
                // metadata served by another OAuth API sharing the gateway.
                authorization_servers = new[] { $"{c.ExternalBaseUrl}/{c.RoutePrefix}" },
                scopes_supported = c.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                bearer_methods_supported = new[] { "header" }
            });
        }).AllowAnonymous();

        // RFC 8414: OAuth Authorization Server Metadata
        app.MapGet("/.well-known/oauth-authorization-server", (IOptions<OAuthConfig> config) =>
        {
            var c = config.Value;
            return Results.Json(new
            {
                // issuer must equal the authorization_servers value advertised above so that
                // clients which validate issuer == discovery URL base accept this document.
                issuer = $"{c.ExternalBaseUrl}/{c.RoutePrefix}",
                authorization_endpoint = $"{c.ExternalBaseUrl}/{c.RoutePrefix}/authorize",
                token_endpoint = $"{c.ExternalBaseUrl}/{c.RoutePrefix}/token",
                registration_endpoint = $"{c.ExternalBaseUrl}/{c.RoutePrefix}/register",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                token_endpoint_auth_methods_supported = new[] { "none", "client_secret_post" },
                code_challenge_methods_supported = new[] { "S256" },
                scopes_supported = c.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            });
        }).AllowAnonymous();

        // RFC 7591: Dynamic Client Registration
        app.MapPost("/register", async (HttpRequest request, AuthorizationStore store) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);
            var clientName = body.TryGetProperty("client_name", out var cn) ? cn.GetString() ?? "Unknown" : "Unknown";
            var redirectUris = body.TryGetProperty("redirect_uris", out var ru)
                ? ru.EnumerateArray().Select(u => u.GetString()!).ToArray()
                : Array.Empty<string>();

            var registration = new ClientRegistration
            {
                ClientId = Guid.NewGuid().ToString("N"),
                ClientName = clientName,
                RedirectUris = redirectUris
            };
            store.StoreRegistration(registration);

            return Results.Json(new
            {
                client_id = registration.ClientId,
                client_name = registration.ClientName,
                redirect_uris = registration.RedirectUris,
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" }
            }, statusCode: 201);
        }).AllowAnonymous();

        // Authorization endpoint — redirects to Entra ID
        app.MapGet("/authorize", (
            HttpRequest request,
            IOptions<OAuthConfig> config,
            AuthorizationStore store,
            ILogger<AuthorizationStore> logger) =>
        {
            var c = config.Value;

            var clientId = request.Query["client_id"].FirstOrDefault() ?? "";
            var redirectUri = request.Query["redirect_uri"].FirstOrDefault() ?? "";
            var state = request.Query["state"].FirstOrDefault() ?? "";
            var codeChallenge = request.Query["code_challenge"].FirstOrDefault() ?? "";
            var codeChallengeMethod = request.Query["code_challenge_method"].FirstOrDefault() ?? "S256";

            if (string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(codeChallenge))
            {
                return Results.BadRequest(new { error = "invalid_request", error_description = "redirect_uri and code_challenge are required" });
            }

            // Generate internal PKCE for our call to Entra ID
            var internalCodeVerifier = PkceHelper.GenerateCodeVerifier();
            var internalCodeChallenge = PkceHelper.ComputeCodeChallenge(internalCodeVerifier);
            var internalState = Guid.NewGuid().ToString("N");

            store.StoreSession(internalState, new AuthSession
            {
                ClientRedirectUri = redirectUri,
                ClientState = state,
                ClientCodeChallenge = codeChallenge,
                ClientCodeChallengeMethod = codeChallengeMethod,
                InternalCodeVerifier = internalCodeVerifier
            });

            logger.LogInformation("OAuth authorize: client={ClientId}, redirecting to Entra ID, internalState={State}", clientId, internalState);

            var callbackUri = $"{c.ExternalBaseUrl}/{c.RoutePrefix}/oauth-callback";
            var entraUrl = $"{c.EntraAuthorizeUrl}" +
                $"?client_id={HttpUtility.UrlEncode(c.ClientId)}" +
                $"&response_type=code" +
                $"&redirect_uri={HttpUtility.UrlEncode(callbackUri)}" +
                $"&scope={HttpUtility.UrlEncode(c.Scopes)}" +
                $"&state={HttpUtility.UrlEncode(internalState)}" +
                $"&code_challenge={HttpUtility.UrlEncode(internalCodeChallenge)}" +
                $"&code_challenge_method=S256";

            return Results.Redirect(entraUrl);
        }).AllowAnonymous();

        // OAuth callback — receives code from Entra ID, exchanges for token, redirects to client
        app.MapGet("/oauth-callback", async (
            HttpRequest request,
            IOptions<OAuthConfig> config,
            AuthorizationStore store,
            IHttpClientFactory httpClientFactory,
            Azure.Identity.ManagedIdentityCredential managedIdentity,
            ILogger<AuthorizationStore> logger) =>
        {
            var c = config.Value;
            var code = request.Query["code"].FirstOrDefault();
            var internalState = request.Query["state"].FirstOrDefault();
            var error = request.Query["error"].FirstOrDefault();

            if (!string.IsNullOrEmpty(error))
            {
                var errorDesc = request.Query["error_description"].FirstOrDefault() ?? error;
                logger.LogWarning("OAuth callback error from Entra ID: {Error} - {Description}", error, errorDesc);
                return Results.BadRequest(new { error, error_description = errorDesc });
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(internalState))
                return Results.BadRequest(new { error = "invalid_request", error_description = "Missing code or state" });

            var session = store.GetAndRemoveSession(internalState);
            if (session == null)
            {
                logger.LogWarning("OAuth callback: session not found for state={State}", internalState);
                return Results.BadRequest(new { error = "invalid_request", error_description = "Session expired or invalid state" });
            }

            // Managed identity token for the client assertion (federated identity credential)
            var miToken = await managedIdentity.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "api://AzureADTokenExchange" }));

            var callbackUri = $"{c.ExternalBaseUrl}/{c.RoutePrefix}/oauth-callback";
            var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = c.ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = callbackUri,
                ["scope"] = c.Scopes,
                ["code_verifier"] = session.InternalCodeVerifier,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = miToken.Token
            });

            var httpClient = httpClientFactory.CreateClient();
            var tokenResponse = await httpClient.PostAsync(c.EntraTokenUrl, tokenRequest);
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync();

            if (!tokenResponse.IsSuccessStatusCode)
            {
                logger.LogError("Entra ID token exchange failed: {Status} {Body}", tokenResponse.StatusCode, tokenJson);
                return Results.BadRequest(new { error = "token_error", error_description = "Failed to exchange code with Entra ID" });
            }

            logger.LogInformation("OAuth callback: token exchange successful, issuing code to client");

            var clientCode = Guid.NewGuid().ToString("N");
            store.StoreCode(clientCode, new IssuedCode
            {
                TokenResponseJson = tokenJson,
                ClientCodeChallenge = session.ClientCodeChallenge,
                ClientCodeChallengeMethod = session.ClientCodeChallengeMethod
            });

            var separator = session.ClientRedirectUri.Contains('?') ? "&" : "?";
            var clientRedirect = $"{session.ClientRedirectUri}{separator}code={HttpUtility.UrlEncode(clientCode)}&state={HttpUtility.UrlEncode(session.ClientState)}";

            return Results.Redirect(clientRedirect);
        }).AllowAnonymous();

        // Token endpoint — MCP client exchanges our code for the Entra ID JWT
        app.MapPost("/token", async (
            HttpRequest request,
            IOptions<OAuthConfig> config,
            AuthorizationStore store,
            IHttpClientFactory httpClientFactory,
            ILogger<AuthorizationStore> logger) =>
        {
            var form = await request.ReadFormAsync();
            var grantType = form["grant_type"].FirstOrDefault() ?? "";

            if (grantType == "refresh_token")
            {
                var c = config.Value;
                var refreshToken = form["refresh_token"].FirstOrDefault() ?? "";
                var refreshRequest = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = c.ClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["scope"] = c.Scopes
                });

                var httpClient = httpClientFactory.CreateClient();
                var refreshResponse = await httpClient.PostAsync(c.EntraTokenUrl, refreshRequest);
                var refreshJson = await refreshResponse.Content.ReadAsStringAsync();

                return Results.Content(refreshJson, "application/json", statusCode: (int)refreshResponse.StatusCode);
            }

            if (grantType != "authorization_code")
                return Results.BadRequest(new { error = "unsupported_grant_type" });

            var code = form["code"].FirstOrDefault() ?? "";
            var codeVerifier = form["code_verifier"].FirstOrDefault() ?? "";

            var issuedCode = store.RedeemCode(code);
            if (issuedCode == null)
            {
                logger.LogWarning("Token endpoint: invalid or expired code");
                return Results.BadRequest(new { error = "invalid_grant", error_description = "Invalid or expired authorization code" });
            }

            if (issuedCode.ClientCodeChallengeMethod == "S256" && !string.IsNullOrEmpty(codeVerifier))
            {
                if (!PkceHelper.ValidateCodeChallenge(codeVerifier, issuedCode.ClientCodeChallenge))
                {
                    logger.LogWarning("Token endpoint: PKCE validation failed");
                    return Results.BadRequest(new { error = "invalid_grant", error_description = "PKCE code_verifier validation failed" });
                }
            }

            logger.LogInformation("Token endpoint: code redeemed successfully");

            return Results.Content(issuedCode.TokenResponseJson, "application/json");
        }).AllowAnonymous();

        return app;
    }
}
