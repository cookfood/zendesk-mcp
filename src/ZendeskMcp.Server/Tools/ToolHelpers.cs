using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Server.Tools;

/// <summary>Small helpers shared by the Zendesk tools.</summary>
internal static class ToolHelpers
{
    public static readonly JsonSerializerOptions IgnoreNulls = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialises a request body, omitting null properties.</summary>
    public static string Serialise(object body) => JsonSerializer.Serialize(body, IgnoreNulls);

    /// <summary>Clamps a per-page value to the Zendesk maximum of 100.</summary>
    public static int ClampPerPage(int perPage) => Math.Clamp(perPage, 1, 100);

    /// <summary>Splits a comma-separated list into a trimmed array, or null if empty.</summary>
    public static string[]? SplitCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    /// <summary>Parses a JSON snippet supplied by the caller, or null if blank. Throws on invalid JSON.</summary>
    public static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON supplied: {ex.Message}");
        }
    }

    public static Dictionary<string, string?> Query(params (string Key, string? Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in pairs)
        {
            if (!string.IsNullOrEmpty(value)) dict[key] = value;
        }
        return dict;
    }
}
