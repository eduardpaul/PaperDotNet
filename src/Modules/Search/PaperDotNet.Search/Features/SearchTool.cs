using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Mcp.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>The <c>search</c> MCP tool (API-08): full-text search over what the caller may read.</summary>
internal sealed class SearchTool(
    SearchDbContext db, IFullTextSearch fullText, IWorkspaceAccess workspaces, IUserDirectory users, ICurrentUser user) : IMcpTool
{
    private const int MaxTop = 50;

    public string Name => "search";

    public string Description =>
        "Full-text search across documents (including their text), tasks, events and list items you can read. " +
        "Query syntax: words, \"phrases\", OR, -exclude, prefix*. Returns ids to read with get_item.";

    public System.Text.Json.JsonElement InputSchema { get; } = McpSchema.ObjectSchema(
        ("query", "string", "What to search for.", true),
        ("workspaceId", "string", "Only this workspace (id).", false),
        ("top", "integer", "Maximum number of hits (1-50, default 10).", false));

    public string? RequiredScope => SearchScopes.Read;

    public bool IsReadOnly => true;

    public async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var query = FullTextQuery.Parse(arguments.GetRequiredString("query"), out var error);
        if (query is null)
        {
            return McpToolResult.Error(error!);
        }

        var top = Math.Clamp(arguments.GetInt32("top") ?? 10, 1, MaxTop);
        var workspaceId = arguments.GetGuid("workspaceId");
        var principals = await SearchEndpoints.PrincipalsAsync(workspaces, users, user, cancellationToken);
        var hits = from d in db.Documents.AsNoTracking()
                   join m in fullText.Match<SearchDocument>(db, query) on d.Id equals m.Id
                   where db.Principals.Any(p => p.DocumentId == d.Id && principals.Contains(p.Principal))
                   select new { Document = d, m.Rank };
        if (workspaceId is { } ws)
        {
            hits = hits.Where(h => h.Document.WorkspaceId == ws);
        }

        var page = await hits.OrderByDescending(h => h.Rank).ThenBy(h => h.Document.Id).Take(top).ToListAsync(cancellationToken);
        var tokens = query.PositiveTokens.ToList();
        return McpToolResult.FromJson(new
        {
            hits = page.Select(h => new
            {
                id = h.Document.Id,
                type = h.Document.SourceType,
                workspaceId = h.Document.WorkspaceId,
                listId = h.Document.ContainerId,
                title = h.Document.Title,
                snippet = SearchEndpoints.Snippet(h.Document.Body, tokens),
                updatedAt = h.Document.UpdatedAt,
            }),
        });
    }
}
