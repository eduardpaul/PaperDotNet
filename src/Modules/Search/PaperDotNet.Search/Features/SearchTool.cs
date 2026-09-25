using PaperDotNet.Mcp.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>The <c>search</c> MCP tool (API-08): keyword, semantic or hybrid search over what the caller may read.</summary>
internal sealed class SearchTool(SearchService search, SemanticSearch semantic) : IMcpTool
{
    private const int MaxTop = 50;

    public string Name => "search";

    public string Description =>
        "Search documents (including their text, with the matching page), tasks, events and list items you can read. " +
        "Query syntax: words, \"phrases\", OR, -exclude, prefix*. " +
        (semantic.Enabled ? "By default it also finds matches by meaning (hybrid); mode can be keyword, semantic or hybrid. " : string.Empty) +
        "Returns ids to read with get_item.";

    public System.Text.Json.JsonElement InputSchema { get; } = McpSchema.ObjectSchema(
        ("query", "string", "What to search for.", true),
        ("workspaceId", "string", "Only this workspace (id).", false),
        ("mode", "string", "keyword, semantic or hybrid (default: hybrid when available).", false),
        ("top", "integer", "Maximum number of hits (1-50, default 10).", false));

    public string? RequiredScope => SearchScopes.Read;

    public bool IsReadOnly => true;

    public async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        SearchMode? mode = null;
        if (arguments.GetString("mode") is { Length: > 0 } requested)
        {
            if (!Enum.TryParse<SearchMode>(requested, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                return McpToolResult.Error("mode must be keyword, semantic or hybrid.");
            }

            mode = value;
        }

        var top = Math.Clamp(arguments.GetInt32("top") ?? 10, 1, MaxTop);
        var (result, _, error) = await search.SearchAsync(
            new SearchRequest(arguments.GetRequiredString("query"), mode, arguments.GetGuid("workspaceId"), Top: top, WithFacets: false), cancellationToken);
        if (result is null)
        {
            return McpToolResult.Error(error!);
        }

        return McpToolResult.FromJson(new
        {
            mode = result.Mode.ToString().ToLowerInvariant(),
            hits = result.Hits.Select(h => new
            {
                id = h.Id,
                type = h.SourceType,
                workspaceId = h.WorkspaceId,
                listId = h.ContainerId,
                title = h.Title,
                snippet = h.Snippet,
                page = h.Page,
                matchedBy = h.MatchedBy,
                updatedAt = h.UpdatedAt,
            }),
        });
    }
}
