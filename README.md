# Zendesk MCP server

A [Model Context Protocol](https://modelcontextprotocol.io) server for Zendesk, written in C# (.NET 10).
It gives Claude (and any other MCP client) access to the Zendesk support desk — tickets, comments,
users, organisations, groups, views, macros and the Help Centre knowledge base — plus a generic
escape hatch that reaches any Zendesk REST API v2 endpoint.

It's a first-party replacement for a third-party Python server. The key differences:

- A much larger tool set, including ticket **search** and **views** (so you can filter, for example,
  the Dev Team's open tickets).
- **Two transports from one build**: `stdio` for running locally (just like the old server), and
  `http` for hosting so it can be used from **Claude web** as well as **Claude Code**.

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

## How authentication works

There are two separate layers, and it helps to keep them apart:

1. **Talking to Zendesk.** The server uses a single **shared service account** — one
   `email` / `api_token` pair, sent as HTTP Basic auth. Every action is attributed to that account.
2. **Talking to the server.** Locally (stdio) there is nothing to authenticate — you run the
   container yourself. When hosted over HTTP, access is gated by an API key (non-production) or by
   **Microsoft Entra ID OAuth 2.1** (production / Claude web), terminated at Azure API Management.

## Configuration

All settings can be supplied as environment variables (handy for `--env-file`) or via
`appsettings.json`.

| Setting | Environment variable | Required | Notes |
| --- | --- | --- | --- |
| Subdomain | `ZENDESK_SUBDOMAIN` | Yes | e.g. `acme` for `acme.zendesk.com` |
| Agent e-mail | `ZENDESK_EMAIL` | Yes | The account actions are attributed to |
| API token | `ZENDESK_API_TOKEN` | Yes | Created in Zendesk Admin Centre |
| Transport | `TRANSPORT` | No | `stdio` (default) or `http` |
| API key | `MCP_API_KEY` | No | HTTP transport, non-production only |

Copy [`.env.example`](.env.example) to `.env` and fill it in.

## Quick start — local Docker (stdio)

This mirrors the original server.

```bash
# Build the image (from the repository root)
docker build -f src/ZendeskMcp.Server/Dockerfile -t zendesk-mcp .

# Run it — the -i flag keeps stdin open for the MCP protocol
docker run --rm -i --env-file .env zendesk-mcp
```

The server starts in `stdio` mode by default, so logs go to stderr and the JSON-RPC protocol uses
stdout.

## Use from Claude Code

The simplest option is stdio via Docker:

```bash
claude mcp add zendesk -- docker run --rm -i --env-file /full/path/to/.env zendesk-mcp
```

Then check it:

```bash
claude mcp list      # zendesk should show "connected"
```

Try `search_tickets` with a query such as `group:"Dev Team" status:open`.

You can also point Claude Code at a hosted instance over HTTP:

```bash
claude mcp add zendesk --transport http https://<host>/mcp --header "X-API-Key:<your key>"
```

## Use from Claude web

Claude web needs a remotely hosted server over HTTPS with OAuth. Once the server is deployed behind
Azure API Management (see [Deployment](#deployment)):

1. In Claude web, go to **Settings → Connectors → Add custom connector**.
2. Enter the server URL, e.g. `https://<your-gateway>/mcp`.
3. Claude will run the OAuth flow against Entra ID and connect.

## Running locally without Docker

```bash
# stdio
dotnet run --project src/ZendeskMcp.Server

# http (listens on http://localhost:8080)
TRANSPORT=http dotnet run --project src/ZendeskMcp.Server
```

## Security notes

- Because there is a single shared Zendesk account, anyone who can reach the server can act as that
  account. Keep the credentials in a secret store and keep the server access-controlled.
- `zendesk_request` can call **any** endpoint, including destructive ones. It is unrestricted by
  default because access is gated by Entra ID and it is an internal tool. To narrow it, restrict the
  allowed HTTP methods in `src/ZendeskMcp.Server/Tools/GenericTools.cs`.
- The write tools (`create_ticket`, `update_ticket`, `create_ticket_comment`, `delete_ticket`)
  change live Zendesk data — the server instructions ask Claude to confirm before using them.

## Project structure

```
src/ZendeskMcp.Server/
  Program.cs            # host + dual-transport switch (stdio / http)
  Auth/                 # OAuth 2.1 bridge to Entra ID + API key middleware
  Zendesk/              # ZendeskClient, options, attachment validation (no org-internal deps)
  Tools/                # the MCP tools
  Prompts/              # analyze-ticket, draft-ticket-response
  Resources/            # zendesk://knowledge-base
  Dockerfile
apim-policies/          # Azure API Management policy + notes
tests/ZendeskMcp.Tests/ # unit tests
```

The `Zendesk/` folder is intentionally free of organisation-internal dependencies, so the Zendesk core
stays portable.

## Development

```bash
dotnet build ZendeskMcp.slnx
dotnet test ZendeskMcp.slnx
```

## Deployment

The server is designed to run as a container on **Azure App Service (for Containers)**, behind **Azure
API Management**, with a dedicated **Microsoft Entra ID app registration** (audience `api://<client-id>`).

- The container runs with `TRANSPORT=http` and `ASPNETCORE_ENVIRONMENT=Production`.
- API Management validates the Entra ID JWT and forwards requests; see
  [`apim-policies/`](apim-policies/) for the policy and the named values it needs.
- Environment-specific values (tenant/client IDs, gateway URL, credentials) are supplied via App Service
  settings / Key Vault — **never committed** to this repo.
- The GitHub Actions workflows under `.github/workflows/` build, test, push the image and deploy.

## Configuration & secrets

All deployment-specific and secret values are provided at runtime through environment variables / your
configuration store — nothing organisation-specific is committed here. See `.env.example` for local use.
