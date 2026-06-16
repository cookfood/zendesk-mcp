using System.Text.Json.Nodes;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Server.Resources;

/// <summary>
/// Builds and caches a snapshot of the Help Centre knowledge base (sections and
/// their articles). Cached for one hour, matching the original Python server.
/// </summary>
public sealed class KnowledgeBaseService(ZendeskClient client)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private const int MaxPages = 50;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<string> GetKnowledgeBaseAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            var result = await BuildAsync(cancellationToken);
            _cached = result;
            _cachedAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> BuildAsync(CancellationToken cancellationToken)
    {
        var sections = await FetchAllAsync("help_center/sections.json", "sections", cancellationToken);

        var knowledgeBase = new JsonObject();
        var totalArticles = 0;

        foreach (var section in sections)
        {
            if (section is not JsonObject sectionObj) continue;
            var sectionId = sectionObj["id"]?.GetValue<long>() ?? 0;
            var name = sectionObj["name"]?.GetValue<string>() ?? sectionId.ToString();

            var articles = await FetchAllAsync($"help_center/sections/{sectionId}/articles.json", "articles", cancellationToken);
            var articleArray = new JsonArray();
            foreach (var article in articles)
            {
                if (article is not JsonObject a) continue;
                articleArray.Add(new JsonObject
                {
                    ["id"] = a["id"]?.DeepClone(),
                    ["title"] = a["title"]?.DeepClone(),
                    ["body"] = a["body"]?.DeepClone(),
                    ["updated_at"] = a["updated_at"]?.DeepClone(),
                    ["url"] = a["html_url"]?.DeepClone(),
                });
                totalArticles++;
            }

            knowledgeBase[name] = new JsonObject
            {
                ["section_id"] = sectionId,
                ["description"] = sectionObj["description"]?.DeepClone(),
                ["articles"] = articleArray,
            };
        }

        var root = new JsonObject
        {
            ["knowledge_base"] = knowledgeBase,
            ["metadata"] = new JsonObject
            {
                ["sections"] = sections.Count,
                ["total_articles"] = totalArticles,
            },
        };

        return root.ToJsonString();
    }

    private async Task<List<JsonNode?>> FetchAllAsync(string path, string arrayKey, CancellationToken cancellationToken)
    {
        var items = new List<JsonNode?>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var query = ToolQuery(page);
            var json = await client.SendAsync(HttpMethod.Get, path, query, cancellationToken: cancellationToken);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root?[arrayKey] is not JsonArray array || array.Count == 0) break;

            foreach (var item in array) items.Add(item?.DeepClone());

            if (root["next_page"] is null || root["next_page"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null) break;
        }
        return items;
    }

    private static Dictionary<string, string?> ToolQuery(int page) => new()
    {
        ["page"] = page.ToString(),
        ["per_page"] = "100",
    };
}
