using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Api;
using PaperDotNet.Mcp.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>The <c>search</c> MCP tool (API-08): keyword search over what the caller may read (semantic search comes with T12e).</summary>
internal sealed class SearchTool(SearchService search, Caller caller) : IMcpTool
{
    private const int MaxTop = 50;

    public string Name => "search";

    public string Description =>
        "Search documents (including their text, with the matching page), tasks, events and list items you can read. " +
        "Query syntax: words, \"phrases\", OR, -exclude, prefix*. Returns ids to read with get_item.";

    public JsonElement InputSchema { get; } = McpSchema.ObjectSchema(
        ("query", "string", "What to search for.", true),
        ("workspaceId", "string", "Only this workspace (id).", false),
        ("top", "integer", $"Maximum number of hits (1-{MaxTop}, default 10).", false));

    public string? RequiredScope => SearchScopes.Read;

    public bool IsReadOnly => true;

    public async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var top = Math.Clamp(arguments.GetInt32("top") ?? 10, 1, MaxTop);
        var (result, _, error) = await search.SearchAsync(
            caller.TenantId, caller.UserId, new SearchRequest(arguments.GetRequiredString("query"), arguments.GetGuid("workspaceId"), Top: top, WithFacets: false),
            cancellationToken);
        if (result is null)
        {
            return McpToolResult.Error(error ?? "The search failed.");
        }

        return McpToolResult.FromJson(new JsonObject
        {
            ["mode"] = "keyword",
            ["hits"] = new JsonArray([.. result.Hits.Select(h => (JsonNode)new JsonObject
            {
                ["id"] = h.Id,
                ["type"] = h.SourceType,
                ["workspaceId"] = h.WorkspaceId,
                ["listId"] = h.ContainerId,
                ["title"] = h.Title,
                ["snippet"] = h.Snippet,
                ["page"] = h.Page,
                ["matchedBy"] = new JsonArray([.. h.MatchedBy.Select(m => (JsonNode)m)]),
                ["updatedAt"] = h.UpdatedAt,
            })]),
        });
    }
}
