# Zendesk MCP server

A [Model Context Protocol](https://modelcontextprotocol.io) server for Zendesk, written in C# (.NET 10).
It gives Claude — and any other MCP client — access to the Zendesk support desk: tickets, comments,
users, organisations, groups, views, macros and the Help Centre knowledge base, plus a generic escape
hatch that reaches any Zendesk REST API v2 endpoint.

> **Credit:** this project grew out of [reminia/zendesk-mcp-server](https://github.com/reminia/zendesk-mcp-server),
> the original Python Zendesk MCP server — thanks to reminia for showing the way.

Highlights:

- A broad tool set, including ticket **search** and **views** (so you can filter, for example, one
  team's open tickets).
- **Two transports from one build**: `stdio` for running locally, and `http` for hosting so it can be
  used from **Claude web** (and remote **Claude Code**) over HTTPS with OAuth.

---

## Contents

- [Tools](#tools)
- [Pick how you want to run it](#pick-how-you-want-to-run-it)
- [Configuration reference](#configuration-reference)
- [Path 1 — Local Docker (stdio)](#path-1--local-docker-stdio)
- [Path 2 — Self-hosted HTTP (API key)](#path-2--self-hosted-http-api-key)
- [Path 3 — Azure: hosted HTTPS with OAuth (Claude web)](#path-3--azure-hosted-https-with-oauth-claude-web)
- [Connecting a client](#connecting-a-client)
- [How authentication works](#how-authentication-works)
- [Troubleshooting](#troubleshooting)
- [Security notes](#security-notes)
- [Project structure & development](#project-structure--development)

---

## Tools

| Area | Tools |
| --- | --- |
| Tickets | `get_ticket`, `get_tickets`, `search_tickets`, `create_ticket`, `update_ticket`, `delete_ticket` |
| Comments | `get_ticket_comments`, `create_ticket_comment` |
| Attachments | `get_ticket_attachment` (images only, validated, 10 MB cap) |
| Users | `get_current_user`, `get_user`, `list_users`, `search_users`, `create_user`, `update_user` |
| Organisations | `get_organization`, `list_organizations`, `search_organizations` |
| Groups | `list_groups`, `get_group` |
| Views | `list_views`, `get_view`, `get_view_tickets` |
| Macros | `list_macros`, `get_macro` |
| Help Centre | `search_articles` (plus the `zendesk://knowledge-base` resource) |
| Anything else | `zendesk_request` — call any Zendesk REST API v2 endpoint directly |

It also provides two prompts: `analyze-ticket` and `draft-ticket-response`.

---

## Pick how you want to run it

There are three ways to run the server. Start at the top — each path is a superset of the one above it.

| # | Path | Transport | Who can connect | Auth to the server | Effort |
| --- | --- | --- | --- | --- | --- |
| 1 | **Local Docker** | `stdio` | Claude Code / any local MCP client on your machine | none (you run it) | minutes |
| 2 | **Self-hosted HTTP** | `http` | anything that can reach the host + has the key | shared API key header | small |
| 3 | **Azure + OAuth** | `http` | **Claude web** and remote clients | Microsoft Entra ID OAuth 2.1 | most |

All three use the **same build and the same Zendesk credentials** (a single shared service account — see
[How authentication works](#how-authentication-works)). The only differences are the transport and how
clients authenticate *to the server*.

> The hosting steps below use **Azure** as the worked example because the OAuth bridge ships ready for
> Microsoft Entra ID. Nothing forces that choice: the container is a plain ASP.NET app, so you can run
> Path 2 on any host (a VM, Kubernetes, Fly.io, Render, …), and the OAuth concepts in Path 3 map onto
> any reverse proxy + identity provider.

---

## Configuration reference

Every setting can be supplied as an **environment variable** (handy with `--env-file`) or via
`appsettings.json`. Environment variables use `__` (double underscore) for nested keys, e.g.
`OAuth__ExternalBaseUrl` ↔ `OAuth:ExternalBaseUrl`.

### Always required (talking to Zendesk)

| Setting | Env var | Example | Notes |
| --- | --- | --- | --- |
| Subdomain | `ZENDESK_SUBDOMAIN` | `acme` | for `acme.zendesk.com` |
| Agent e-mail | `ZENDESK_EMAIL` | `agent@acme.com` | the account actions are attributed to |
| API token | `ZENDESK_API_TOKEN` | `…` | created in Zendesk Admin Centre → Apps and integrations → APIs → Zendesk API |

Copy [`.env.example`](.env.example) to `.env` and fill these in.

### HTTP transport

| Setting | Env var | Required when | Notes |
| --- | --- | --- | --- |
| Transport | `TRANSPORT` | always (`http`) | `stdio` (default) or `http` |
| API key | `MCP_API_KEY` (`Mcp:ApiKey`) | non-production HTTP | shared secret; sent by clients as the `X-API-Key` header. **Ignored in Production** (Production uses OAuth instead — see below). |

### Production OAuth (Path 3 only)

In Production the API-key middleware is **off** and the server validates Microsoft Entra ID JWTs and
runs the OAuth bridge. These map to the `AzureAd` and `OAuth` config sections:

| Setting | Env var | Example | Notes |
| --- | --- | --- | --- |
| Tenant ID | `AzureAd__TenantId` | `<tenant-guid>` | your Entra tenant |
| Client ID | `AzureAd__ClientId` | `<client-guid>` | the app registration |
| Audience | `AzureAd__Audience` | `api://<client-guid>` | token audience the API accepts |
| OAuth client ID | `OAuth__ClientId` | `<client-guid>` | same app registration |
| OAuth tenant ID | `OAuth__TenantId` | `<tenant-guid>` | |
| OAuth scopes | `OAuth__Scopes` | `api://<client-guid>/mcp.read openid profile offline_access` | space-delimited; include `offline_access` for refresh tokens |
| External base URL | `OAuth__ExternalBaseUrl` | `https://your-gateway.example.com` | the **public** gateway origin (no trailing path) |
| Route prefix | `OAuth__RoutePrefix` | `zendesk-mcp` | the public path the server is exposed under — **must match your gateway API path** (default `mcp`) |

`ExternalBaseUrl` + `RoutePrefix` are how the server advertises its own OAuth URLs, e.g.
`https://your-gateway.example.com/zendesk-mcp/authorize`. Getting these two right is the single most
important part of Path 3 — see [the OAuth discovery note](#oauth-discovery-on-a-shared-gateway).

---

## Path 1 — Local Docker (stdio)

The simplest option, and all you need for Claude Code on your own machine.

```bash
# Build the image (from the repository root)
docker build -f src/ZendeskMcp.Server/Dockerfile -t zendesk-mcp .

# Run it — the -i flag keeps stdin open for the MCP protocol
docker run --rm -i --env-file .env zendesk-mcp
```

The server starts in `stdio` mode by default: logs go to stderr, and the JSON-RPC protocol uses stdout.
There is nothing to authenticate — you launched the process, so you already have access.

Prefer no Docker?

```bash
dotnet run --project src/ZendeskMcp.Server          # stdio
```

Jump to [Connecting a client](#connecting-a-client) to wire it into Claude Code.

---

## Path 2 — Self-hosted HTTP (API key)

Run the same image in HTTP mode behind any host you control. A shared API key gates access. This is
ideal for a shared team server, a remote Claude Code setup, or any non-browser MCP client.

```bash
docker run --rm \
  -e TRANSPORT=http \
  -e MCP_API_KEY=choose-a-strong-secret \
  -p 8080:8080 \
  --env-file .env \
  zendesk-mcp
```

The server listens on `:8080`. Clients must send `X-API-Key: <your secret>` on every request. Health
check: `GET /healthz` (always anonymous).

Put it behind TLS (a reverse proxy such as Caddy, nginx, or your platform's ingress) before exposing it
beyond localhost. That's everything most self-hosters need.

> **Why not use the API key with Claude web?** Claude web only connects to MCP servers that implement
> the OAuth flow — it has no place to put a static header. For Claude web you need Path 3.

---

## Path 3 — Azure: hosted HTTPS with OAuth (Claude web)

This is the full setup: the container runs in **Production** mode, a reverse proxy / gateway terminates
TLS and validates OAuth tokens, and the server itself acts as a small **OAuth 2.1 authorization server**
that bridges to your identity provider. The worked example uses **Azure App Service + Azure API
Management (APIM) + Microsoft Entra ID**, which the code supports out of the box.

```
Claude web ──HTTPS──▶ API Management ──▶ App Service (this container, TRANSPORT=http, Production)
   │                  • validates Entra JWT          │
   │                  • CORS for the browser          ├─ /<prefix>/.well-known/...  (OAuth discovery)
   └─ OAuth 2.1 ◀──────────────────────────────────── ├─ /<prefix>/authorize|token|register|oauth-callback
      (bridged to Entra ID)                            └─ /<prefix>  (the MCP endpoint, JWT-protected)
```

### Step 1 — Create the Entra ID app registration

In **Microsoft Entra ID → App registrations → New registration**:

1. **Expose an API**: set the Application ID URI to `api://<client-id>`.
2. Add a **delegated scope**, e.g. `mcp.read`. This becomes part of `OAuth__Scopes`.
3. Under **Authentication**, add a **Web** redirect URI:
   `https://your-gateway.example.com/<route-prefix>/oauth-callback`
   (e.g. `.../zendesk-mcp/oauth-callback`). This must exactly match `ExternalBaseUrl` + `RoutePrefix`.
4. *(Do this last, after Step 2.)* The server exchanges the auth code with Entra using a **federated
   identity credential** rather than a client secret. Once the App Service exists, add a federated
   credential on the app registration whose subject is the **App Service's managed identity**. (See
   `Auth/OAuthEndpoints.cs` — the callback and the token endpoint's refresh grant use
   `ManagedIdentityCredential` + a client assertion.) No client secret to store or rotate.

### Step 2 — Deploy the container to App Service

Create an **App Service (Linux, container)** and deploy this image (the included GitHub Actions workflow
can do it — see Step 4). Set these **application settings**:

```
TRANSPORT=http
ASPNETCORE_ENVIRONMENT=Production
AzureAd__TenantId=<tenant-guid>
AzureAd__ClientId=<client-guid>
AzureAd__Audience=api://<client-guid>
OAuth__ClientId=<client-guid>
OAuth__TenantId=<tenant-guid>
OAuth__Scopes=api://<client-guid>/mcp.read openid profile offline_access
OAuth__ExternalBaseUrl=https://your-gateway.example.com
OAuth__RoutePrefix=zendesk-mcp
ZENDESK_SUBDOMAIN=...      # ideally Key Vault references
ZENDESK_EMAIL=...
ZENDESK_API_TOKEN=...
```

Enable the App Service's **system-assigned managed identity** (needed by Step 1.4). Keep the App Service
private to the gateway if you can (access restrictions / private endpoint), so the only public door is
APIM.

### Step 3 — Put API Management in front

1. **Import an API** in APIM with the **API URL suffix = your route prefix** (e.g. `zendesk-mcp`). This
   *must* equal `OAuth__RoutePrefix`.
2. Set the API's **backend (`serviceUrl`)** to the App Service, e.g.
   `https://your-app.azurewebsites.net`.
3. Apply the inbound policy from [`apim-policies/zendesk-mcp-policy.xml`](apim-policies/zendesk-mcp-policy.xml).
   It: allows CORS for `https://claude.ai`; lets the OAuth discovery/flow paths through **without** JWT;
   requires a valid Entra JWT on every other request; injects the backend `X-API-Key`; and on a 401
   returns a `WWW-Authenticate` header pointing at the protected-resource metadata so the client knows
   how to start the OAuth flow.
4. Define the **named values** the policy references (see
   [`apim-policies/README.md`](apim-policies/README.md)):

   | Named value | What it is |
   | --- | --- |
   | `EntraIDTenantId` | your tenant ID |
   | `ZendeskMcpClientId` | the app registration's client ID (audience `api://<client-id>`) |
   | `ZendeskMcpApiKey` | the shared `X-API-Key` the backend expects (`Mcp:ApiKey`); store in Key Vault |
   | `APIMGatewayURL` | the public gateway origin, e.g. `https://your-gateway.example.com` |

   Use server-specific names (the `ZendeskMcp…` prefix) so multiple MCP APIs on one gateway don't collide.

> **CORS gotcha:** the 401 error response **must** carry CORS headers (the policy's `<on-error>` block
> does this). If it doesn't, the browser hides the `WWW-Authenticate` header and Claude web never starts
> the OAuth flow. This is the most common "it just silently doesn't connect" cause.

### OAuth discovery on a shared gateway

This is subtle and worth understanding, because it's the difference between "connects" and "fails with a
confusing error". When a client connects, it discovers where to authenticate like this:

1. Hits the MCP endpoint → gets `401` with `WWW-Authenticate: …resource_metadata="…/<prefix>/.well-known/oauth-protected-resource"`.
2. Fetches that **protected-resource** document → reads its `authorization_servers` entry.
3. Treats that entry as the **issuer** and fetches the **authorization-server metadata** to learn the
   `authorize` / `token` / `register` endpoints.

Two rules make step 2–3 work when several servers share one gateway host:

- **Advertise the issuer *with* your route prefix.** The server sets `authorization_servers` and
  `issuer` to `{ExternalBaseUrl}/{RoutePrefix}` (e.g. `https://your-gateway.example.com/zendesk-mcp`),
  **not** the bare host. If you advertise the bare host, a client resolves the gateway-root metadata —
  which may belong to a *different* server on the same gateway — and authenticates against the wrong
  backend.
- **Serve the metadata where a path-issuer client looks for it.** Per RFC 8414, for an issuer with a
  path the client requests the **insertion** URL
  `https://host/.well-known/oauth-authorization-server/<prefix>` (the well-known segment goes *before*
  the prefix), not `https://host/<prefix>/.well-known/oauth-authorization-server`. The app serves the
  document at its own `/.well-known/oauth-authorization-server` (which APIM exposes at the path-append
  URL). If your gateway routes the **insertion** URL elsewhere (e.g. to another API), add a small route
  that forwards `…/.well-known/oauth-authorization-server/<prefix>` to this app's
  `/.well-known/oauth-authorization-server`. If your server is alone on its own host/origin, neither
  issue arises — the bare-host issuer just works.

Verify all three resolve to **your** server before connecting a client:

```bash
PREFIX=zendesk-mcp ; HOST=https://your-gateway.example.com

curl -s "$HOST/$PREFIX/.well-known/oauth-protected-resource"           # authorization_servers should end in /$PREFIX
curl -s "$HOST/.well-known/oauth-authorization-server/$PREFIX"         # insertion path — must return YOUR metadata
curl -s "$HOST/$PREFIX/.well-known/oauth-authorization-server"         # append path — same metadata
```

All should show `issuer`/endpoints under `…/$PREFIX/…`. A `404` on the insertion path is the classic
cause of a client failing client registration after discovery.

### Step 4 — Continuous deployment (optional)

The workflows under [`.github/workflows/`](.github/workflows/) build, test and deploy. `deploy.yml`
publishes to App Service on push to `main`. Before it works you must provide credentials — either:

- a **publish profile**: set repo secret `AZURE_WEBAPP_PUBLISH_PROFILE` and variable `AZURE_WEBAPP_NAME`
  (the App Service name); or
- an **`azure/login`** step with a federated (OIDC) service principal.

Without one of these the deploy step fails with `No credentials found` even though build and tests pass.

### Step 5 — Connect Claude web

In Claude web: **Settings → Connectors → Add custom connector**, enter
`https://your-gateway.example.com/<route-prefix>`, and Claude runs the OAuth flow against Entra ID and
connects.

---

## Connecting a client

### Claude Code — local stdio (Path 1)

```bash
claude mcp add zendesk -- docker run --rm -i --env-file /full/path/to/.env zendesk-mcp
claude mcp list      # zendesk should show "connected"
```

### Claude Code — hosted HTTP

```bash
# Path 2 (API key):
claude mcp add zendesk --transport http https://your-host/<prefix> --header "X-API-Key:<your key>"

# Path 3 (OAuth): add the URL, then run /mcp in Claude Code and choose Authenticate
claude mcp add zendesk --transport http https://your-gateway.example.com/<route-prefix>
```

### Claude web (Path 3)

Settings → Connectors → Add custom connector → the server URL. See [Step 5](#step-5--connect-claude-web).

### Any other MCP client

Point it at the stdio command (Path 1) or the HTTP URL (Paths 2/3). For HTTP+OAuth, the client needs to
support the MCP OAuth flow; for HTTP+API-key it just needs to send the `X-API-Key` header.

Once connected, try `search_tickets` with a query like `group:"Dev Team" status:open`.

---

## How authentication works

Two separate layers — keep them apart:

1. **Talking to Zendesk.** The server uses one **shared service account** (an `email` / `api_token`
   pair, HTTP Basic auth). Every action is attributed to that account. This is the same in all three
   paths.
2. **Talking to the server.**
   - **stdio (Path 1):** nothing to authenticate — you run the process.
   - **http, non-Production (Path 2):** the `ApiKeyAuthMiddleware` checks the `X-API-Key` header against
     `MCP_API_KEY`.
   - **http, Production (Path 3):** the API-key middleware is disabled. The gateway validates a
     Microsoft Entra ID JWT, and the server exposes an OAuth 2.1 bridge (`Auth/OAuthEndpoints.cs`):
     it presents itself as the authorization server (metadata + dynamic client registration), and
     bridges the flow to Entra, keeping two PKCE layers — one with the MCP client and an internal one
     with Entra. Refresh grants are bridged the same way: the server forwards the client's refresh
     token to Entra together with a managed-identity client assertion, so sessions renew without
     re-prompting (this requires `offline_access` in `OAuth__Scopes` — without it Entra never issues
     a refresh token).

---

## Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Client error: *Invalid OAuth error response… Unrecognized token `<`* | The OAuth/MCP request reached a backend returning an HTML error page (stopped/cold app, or wrong `serviceUrl`). | Confirm the gateway's backend points at a **running** app; check the App Service is up. |
| Discovery resolves the **wrong** server's metadata | `authorization_servers` advertised as the bare host on a shared gateway. | Set `OAuth__ExternalBaseUrl` + `OAuth__RoutePrefix` so the issuer carries the path; see [discovery note](#oauth-discovery-on-a-shared-gateway). |
| Client fails right after discovery (e.g. `client_uri` / metadata not found) | Auth-server metadata not served at the RFC 8414 **insertion** URL. | Add the insertion-path route; verify with the three `curl`s above. |
| Claude web silently never starts OAuth | 401 response missing CORS headers. | Ensure the policy's `<on-error>` block sets the CORS headers (it does by default). |
| Client error: 404 `Session not found` (JSON-RPC `-32001`) | The client is replaying an `Mcp-Session-Id` the server no longer holds. Stateful sessions live in App Service process memory, so they are lost on restart or scale-out and expire after the transport's `IdleTimeout` (2 hours by default). | The HTTP transport now runs with `Stateless = true`, so no session id is issued or required. A client stuck on a cached id needs the connector removed and re-added once. |
| Users must re-authenticate roughly every hour | No refresh token issued (`offline_access` missing from the deployed `OAuth__Scopes`), or Entra rejects the refresh grant (`AADSTS7000218`) because it lacks a client assertion. | Include `offline_access` in `OAuth__Scopes` and re-authenticate once; ensure the build sends the managed-identity client assertion on refresh. |
| Deploy fails: `No credentials found` | CD not wired. | Set `AZURE_WEBAPP_PUBLISH_PROFILE` + `AZURE_WEBAPP_NAME`, or add `azure/login`. |
| Startup warns *Zendesk credentials are not fully configured* | Missing Zendesk env vars. | Set `ZENDESK_SUBDOMAIN`, `ZENDESK_EMAIL`, `ZENDESK_API_TOKEN`. |
| Values look right but auth to Zendesk fails from Docker | `.env` saved with CRLF line endings leaking `\r` (the code trims, but double-check). | Save `.env` with LF line endings. |

---

## Security notes

- Because there is a single shared Zendesk account, anyone who can reach the server can act as that
  account. Keep the credentials in a secret store and keep the server access-controlled.
- `zendesk_request` can call **any** endpoint, including destructive ones. It is unrestricted by default
  because hosted access is gated by OAuth. To narrow it, restrict the allowed HTTP methods in
  `src/ZendeskMcp.Server/Tools/GenericTools.cs`.
- The write tools (`create_ticket`, `update_ticket`, `create_ticket_comment`, `delete_ticket`) change
  live Zendesk data — the server instructions ask the model to confirm before using them.
- Never commit secrets. `.env` is git-ignored; deployment secrets belong in your platform's config
  store / Key Vault, and APIM named values for secrets should be Key Vault-backed.

---

## Project structure & development

```
src/ZendeskMcp.Server/
  Program.cs            # host + dual-transport switch (stdio / http)
  Auth/                 # OAuth 2.1 bridge to Entra ID + API key middleware
  Zendesk/              # ZendeskClient, options, attachment validation (no host-internal deps)
  Tools/                # the MCP tools
  Prompts/              # analyze-ticket, draft-ticket-response
  Resources/            # zendesk://knowledge-base
  Dockerfile
apim-policies/          # API Management policy + notes
tests/ZendeskMcp.Tests/ # unit tests
```

The `Zendesk/` folder is intentionally free of host-internal dependencies, so the Zendesk core stays
portable.

```bash
dotnet build ZendeskMcp.slnx
dotnet test ZendeskMcp.slnx
```
