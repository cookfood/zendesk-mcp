using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// A thin, self-contained client over the Zendesk REST API v2. It deliberately
/// has no organisation-internal dependencies.
///
/// Authentication uses HTTP Basic auth in the documented
/// <c>{email}/token:{api_token}</c> form. The e-mail determines which agent actions
/// are attributed to: the shared <see cref="ZendeskOptions.Email"/> by default, or the
/// agent mapped to the signed-in user when an <see cref="IZendeskAgentContext"/> is
/// supplied (see <see cref="UserLoginMode.Mapped"/>).
/// </summary>
public sealed class ZendeskClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ZendeskOptions _options;
    private readonly IZendeskAgentContext? _agentContext;
    private readonly string _sharedAuthHeader;

    public ZendeskClient(
        IHttpClientFactory httpClientFactory,
        IOptions<ZendeskOptions> options,
        IZendeskAgentContext? agentContext = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _agentContext = agentContext;
        _sharedAuthHeader = EncodeAuthHeader(_options.Email);
    }

    /// <summary>
    /// Sends a request to a Zendesk API v2 endpoint and returns the raw JSON
    /// response body as a string. <paramref name="path"/> is relative to
    /// <c>/api/v2</c>, e.g. <c>"tickets/123.json"</c> or <c>"search.json"</c>.
    /// </summary>
    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        object? jsonBody = null,
        CancellationToken cancellationToken = default)
    {
        HttpContent? content = null;
        if (jsonBody is not null)
        {
            var json = jsonBody is string s ? s : JsonSerializer.Serialize(jsonBody);
            content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await SendCoreAsync(method, path, query, content, cancellationToken);
    }

    /// <summary>
    /// Uploads a file to Zendesk (<c>POST /api/v2/uploads.json</c>) and returns the upload
    /// token. Passing the token in a comment's <c>uploads</c> array attaches the file to
    /// that comment; Zendesk discards unused tokens after 60 minutes.
    /// </summary>
    public async Task<string> UploadAsync(
        string fileName,
        byte[] data,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var query = new Dictionary<string, string?> { ["filename"] = fileName };
        var body = await SendCoreAsync(HttpMethod.Post, "uploads.json", query, content, cancellationToken);

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("upload", out var upload)
            && upload.TryGetProperty("token", out var token)
            && token.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        throw new InvalidOperationException($"Zendesk accepted the upload of '{fileName}' but returned no upload token.");
    }

    private async Task<string> SendCoreAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string?>? query,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var requestContent = content;
        var authHeader = GetAuthHeader();
        var url = BuildUrl(path, query);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = requestContent;

        var client = _httpClientFactory.CreateClient(nameof(ZendeskClient));
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ZendeskApiException(response.StatusCode, method.Method, path, Truncate(body, 2000));
        }

        return string.IsNullOrEmpty(body) ? "{}" : body;
    }

    /// <summary>
    /// Downloads a ticket attachment from its <c>content_url</c>. The first request
    /// carries the Zendesk auth header; if Zendesk redirects to its content CDN we
    /// follow the redirect <em>without</em> the auth header, as the CDN rejects it.
    /// </summary>
    public async Task<(byte[] Data, string ContentType)> DownloadAttachmentAsync(
        string contentUrl,
        CancellationToken cancellationToken = default)
    {
        var authHeader = GetAuthHeader();
        var client = _httpClientFactory.CreateClient($"{nameof(ZendeskClient)}.NoRedirect");

        using var first = new HttpRequestMessage(HttpMethod.Get, contentUrl);
        first.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        using var firstResponse = await client.SendAsync(first, cancellationToken);

        HttpResponseMessage finalResponse = firstResponse;
        HttpResponseMessage? redirectResponse = null;
        try
        {
            if ((int)firstResponse.StatusCode is >= 300 and < 400 && firstResponse.Headers.Location is { } location)
            {
                // Follow the redirect to the CDN with no Authorization header.
                using var redirect = new HttpRequestMessage(HttpMethod.Get, location);
                redirectResponse = await client.SendAsync(redirect, cancellationToken);
                finalResponse = redirectResponse;
            }

            if (!finalResponse.IsSuccessStatusCode)
            {
                throw new ZendeskApiException(finalResponse.StatusCode, "GET", contentUrl, "attachment download failed");
            }

            var data = await finalResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentType = finalResponse.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return (data, contentType);
        }
        finally
        {
            redirectResponse?.Dispose();
        }
    }

    /// <summary>
    /// Returns the Basic auth value for the current request. With an agent context, the
    /// e-mail is resolved per request; if the caller has no mapped agent the request is
    /// refused here, before anything is sent to Zendesk.
    /// </summary>
    private string GetAuthHeader()
    {
        if (_agentContext is null) return _sharedAuthHeader;

        var agentEmail = _agentContext.AgentEmail;
        if (string.IsNullOrWhiteSpace(agentEmail))
        {
            throw new InvalidOperationException(
                "No Zendesk agent is mapped to the signed-in user, so the request was not sent to Zendesk.");
        }

        return EncodeAuthHeader(agentEmail);
    }

    private string EncodeAuthHeader(string email) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}/token:{_options.ApiToken}"));

    private string BuildUrl(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var trimmed = path.TrimStart('/');
        var url = $"{_options.BaseUrl}/{trimmed}";

        if (query is { Count: > 0 })
        {
            var pairs = query
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
                .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value!)}");
            var queryString = string.Join("&", pairs);
            if (queryString.Length > 0)
            {
                url += url.Contains('?') ? "&" : "?";
                url += queryString;
            }
        }

        return url;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
