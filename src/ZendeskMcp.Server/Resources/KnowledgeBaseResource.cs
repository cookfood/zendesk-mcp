using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Server.Resources;

[McpServerResourceType]
public static class KnowledgeBaseResource
{
    [McpServerResource(UriTemplate = "zendesk://knowledge-base", Name = "knowledge-base", MimeType = "application/json")]
    [Description("All Help Centre (knowledge base) articles, organised by section. Cached for one hour.")]
    public static Task<string> GetKnowledgeBase(
        KnowledgeBaseService knowledgeBase,
        CancellationToken cancellationToken)
        => knowledgeBase.GetKnowledgeBaseAsync(cancellationToken);
}
