# API Management policy

[`zendesk-mcp-policy.xml`](zendesk-mcp-policy.xml) is the inbound policy for the Zendesk MCP API in
Azure API Management. It fronts the App Service container and makes the server usable from Claude web.

What it does:

- Allows CORS for `https://claude.ai`.
- Lets the OAuth discovery and flow paths through unauthenticated (`/.well-known/...`, `/authorize`,
  `/oauth-callback`, `/token`, `/register`) — these run before the client has a token.
- Requires a valid **Microsoft Entra ID** JWT on every other (MCP protocol) request.
- Adds the shared `X-API-Key` header so the backend container can apply its own gate.
- On a 401, returns a `WWW-Authenticate` header pointing at the protected-resource metadata so the
  client knows how to start the OAuth flow.

## Named values to define in APIM

| Named value | Description |
| --- | --- |
| `ZendeskMcpClientId` | Client ID of this server's Entra app registration (audience is `api://<client-id>`) |
| `ZendeskMcpApiKey` | secret — the shared key the backend expects (`Mcp:ApiKey` / `MCP_API_KEY`); store in Key Vault |
| `EntraIDTenantId` | Tenant ID. May be shared with other APIs on the same gateway. |
| `APIMGatewayURL` | Public gateway base URL. May be shared with other APIs on the same gateway. |

Use server-specific names (e.g. `ZendeskMcp…`) for the client ID and API key so they don't collide with
other MCP APIs that may already define `EntraIDClientId` on the same APIM instance.

> Concrete values are environment-specific and are **not** committed here — set them as APIM named
> values in your deployment. (For this deployment they live in the internal deploy notes, not the repo.)

## Important: CORS on the error response

The 401 error response **must** carry the CORS headers (see the `<on-error>` block). If it does not,
the browser blocks the response and Claude web cannot read the `WWW-Authenticate` header — so the
OAuth flow never starts. This is the single biggest gotcha to watch for.

## Entra app registration

Create a **dedicated** app registration for this resource:

- Expose an API and note its application ID URI (`api://<client-id>`).
- Add a delegated scope (e.g. `mcp.read`) and set `OAuth:Scopes` accordingly.
- Add the Web redirect URI `<gateway>/mcp/oauth-callback`.
- After the container is deployed, add a **federated identity credential** whose subject is the App
  Service's managed identity, so the server-to-server token exchange works (see `Auth/OAuthEndpoints.cs`).
  The managed identity doesn't exist until deployment, so this step comes last.
