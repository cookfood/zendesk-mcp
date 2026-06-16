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
/// Authentication uses the shared service account via HTTP Basic auth in the
/// documented <c>{email}/token:{api_token}</c> form. The e-mail determines which
/// agent actions are attributed to.
/// </summary>
public sealed class ZendeskClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ZendeskOptions _options;
    private readonly string _authHeader;

    public ZendeskClient(IHttpClientFactory httpClientFactory, IOptions<ZendeskOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;

        var raw = $"{_options.Email}/token:{_options.ApiToken}";
        _authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
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
        var url = BuildUrl(path, query);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (jsonBody is not null)
        {
            var json = jsonBody is string s ? s : JsonSerializer.Serialize(jsonBody);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

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
        var client = _httpClientFactory.CreateClient($"{nameof(ZendeskClient)}.NoRedirect");

        using var first = new HttpRequestMessage(HttpMethod.Get, contentUrl);
        first.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authHeader);
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
