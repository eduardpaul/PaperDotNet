using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Data;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Search.Features;

public sealed record SearchHit(
    Guid Id,
    string SourceType,
    Guid WorkspaceId,
    Guid? ContainerId,
    Guid? ContentTypeId,
    string Title,
    string? Snippet,
    double Rank,
    Guid? CreatedBy,
    DateTimeOffset UpdatedAt);

public sealed record FacetValue(Guid Value, int Count);

public sealed record SearchFacets(
    IReadOnlyList<FacetValue> Workspace, IReadOnlyList<FacetValue> Container, IReadOnlyList<FacetValue> ContentType, IReadOnlyList<FacetValue> Term);

public sealed record SearchResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<SearchHit> Value,
    [property: JsonPropertyName("@odata.count")] int Count,
    [property: JsonPropertyName("facets")] SearchFacets Facets,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

public sealed record ReindexResponse(Guid Id, OperationStatus Status);

/// <summary>Unified search (SRC-01…04): full text, filters with facets, only what the caller may read.</summary>
internal static class SearchEndpoints
{
    public const int DefaultTop = 25;
    public const int MaxTop = 100;
    private const int FacetSize = 20;
    private const int SnippetRadius = 80;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("search", "Search");
        group.MapGet("", SearchAsync).RequireScope(SearchScopes.Read).WithName("Search");
        group.MapPost("/reindex", ReindexAsync).RequireScope(SearchScopes.Manage).WithName("ReindexSearch");
    }

    /// <summary>
    /// <c>q</c>: words, <c>"phrases"</c>, <c>OR</c>, <c>-exclude</c>, <c>prefix*</c> (optional when filtering).
    /// Filters: <c>workspaceId</c>, <c>containerId</c> (list), <c>contentTypeId</c>, <c>termId</c> (includes child
    /// terms), <c>createdBy</c>, <c>updatedFrom</c>/<c>updatedTo</c>. Paging with <c>$top</c>/<c>$skip</c>.
    /// </summary>
    private static async Task<Results<Ok<SearchResponse>, ValidationProblem>> SearchAsync(
        string? q, Guid? workspaceId, Guid? containerId, Guid? contentTypeId, Guid? termId, Guid? createdBy,
        DateTimeOffset? updatedFrom, DateTimeOffset? updatedTo,
        HttpRequest http, SearchDbContext db, IFullTextSearch fullText, ITermStore terms, IWorkspaceAccess workspaces,
        IUserDirectory users, ICurrentUser user, CancellationToken ct)
    {
        var top = ParseInt(http, "$top", DefaultTop, 1, MaxTop);
        var skip = ParseInt(http, "$skip", 0, 0, int.MaxValue);

        FullTextQuery? query = null;
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = FullTextQuery.Parse(q, out var error);
            if (query is null)
            {
                return ApiErrors.Validation(new Dictionary<string, string[]> { ["q"] = [error!] });
            }
        }
        else if (workspaceId is null && containerId is null && contentTypeId is null && termId is null && createdBy is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["q"] = ["Enter a query or at least one filter."] });
        }

        var principals = await PrincipalsAsync(workspaces, users, user, ct);
        var hits = query is null
            ? db.Documents.AsNoTracking().Select(d => new { Document = d, Rank = 0.0 })
            : from d in db.Documents.AsNoTracking()
              join m in fullText.Match<SearchDocument>(db, query) on d.Id equals m.Id
              select new { Document = d, m.Rank };

        hits = hits.Where(h => db.Principals.Any(p => p.DocumentId == h.Document.Id && principals.Contains(p.Principal)));
        if (workspaceId is { } ws)
        {
            hits = hits.Where(h => h.Document.WorkspaceId == ws);
        }

        if (containerId is { } container)
        {
            hits = hits.Where(h => h.Document.ContainerId == container);
        }

        if (contentTypeId is { } contentType)
        {
            hits = hits.Where(h => h.Document.ContentTypeId == contentType);
        }

        if (createdBy is { } author)
        {
            hits = hits.Where(h => h.Document.CreatedBy == author);
        }

        if (updatedFrom is { } from)
        {
            hits = hits.Where(h => h.Document.UpdatedAt >= from);
        }

        if (updatedTo is { } to)
        {
            hits = hits.Where(h => h.Document.UpdatedAt < to);
        }

        if (termId is { } term)
        {
            // Hierarchical: a term also matches documents tagged with its children.
            var subtree = (await terms.GetDescendantsAsync([term], ct)).GetValueOrDefault(term) ?? [term];
            hits = hits.Where(h => db.Tags.Any(t => t.DocumentId == h.Document.Id && subtree.Contains(t.TermId)));
        }

        var count = await hits.CountAsync(ct);
        var ids = hits.Select(h => h.Document.Id);
        var facets = new SearchFacets(
            await FacetAsync(hits, h => h.Document.WorkspaceId, ct),
            await FacetAsync(hits.Where(h => h.Document.ContainerId != null), h => h.Document.ContainerId!.Value, ct),
            await FacetAsync(hits.Where(h => h.Document.ContentTypeId != null), h => h.Document.ContentTypeId!.Value, ct),
            await FacetAsync(db.Tags.Where(t => ids.Contains(t.DocumentId)), t => t.TermId, ct));

        var ordered = query is null
            ? hits.OrderByDescending(h => h.Document.UpdatedAt).ThenBy(h => h.Document.Id)
            : hits.OrderByDescending(h => h.Rank).ThenBy(h => h.Document.Id);
        var page = await ordered.Skip(skip).Take(top).ToListAsync(ct);
        var tokens = query?.PositiveTokens.ToList() ?? [];
        var value = page.Select(h => new SearchHit(
            h.Document.Id, h.Document.SourceType, h.Document.WorkspaceId, h.Document.ContainerId, h.Document.ContentTypeId,
            h.Document.Title, Snippet(h.Document.Body, tokens), h.Rank, h.Document.CreatedBy, h.Document.UpdatedAt)).ToList();

        string? nextLink = null;
        if (skip + top < count)
        {
            var queryString = http.Query
                .Where(p => p.Key is not "$skip")
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value.ToString())}")
                .Append($"$skip={(skip + top).ToString(CultureInfo.InvariantCulture)}");
            nextLink = $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', queryString)}";
        }

        return TypedResults.Ok(new SearchResponse(value, count, facets, nextLink));
    }

    /// <summary>Rebuilds the index of the tenant from every source, as an operation (admins).</summary>
    private static async Task<Accepted<ReindexResponse>> ReindexAsync(IOperations operations, CancellationToken ct)
    {
        var id = await operations.StartAsync(ReindexOperation.OperationType, new ReindexPayload(), ct);
        return TypedResults.Accepted($"{ApiRoutes.V1}/operations/{id}", new ReindexResponse(id, OperationStatus.NotStarted));
    }

    /// <summary>The caller's principals: user, groups, and workspace memberships (owners also as owners).</summary>
    private static async Task<List<string>> PrincipalsAsync(IWorkspaceAccess workspaces, IUserDirectory users, ICurrentUser user, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return [];
        }

        var principals = new List<string> { SearchPrincipals.User(userId) };
        principals.AddRange((await users.GetGroupIdsAsync(userId, ct)).Select(SearchPrincipals.Group));
        foreach (var membership in await workspaces.GetMyWorkspacesAsync(ct))
        {
            principals.Add(SearchPrincipals.WorkspaceMember(membership.WorkspaceId));
            if (membership.Level == WorkspaceAccessLevel.Manage)
            {
                principals.Add(SearchPrincipals.WorkspaceOwner(membership.WorkspaceId));
            }
        }

        return principals;
    }

    /// <summary>The most frequent values of <paramref name="key"/> with their counts.</summary>
    private static async Task<List<FacetValue>> FacetAsync<T>(IQueryable<T> source, Expression<Func<T, Guid>> key, CancellationToken ct) =>
        (await source.GroupBy(key)
            .Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(v => v.Count).ThenBy(v => v.Key)
            .Take(FacetSize)
            .ToListAsync(ct))
        .Select(v => new FacetValue(v.Key, v.Count))
        .ToList();

    /// <summary>Text around the first matching word (or the start of the body).</summary>
    internal static string? Snippet(string body, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        var position = tokens
            .Select(t => body.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, position - SnippetRadius);
        var end = Math.Min(body.Length, position + SnippetRadius);
        var text = body[start..end].ReplaceLineEndings(" ").Trim();
        return (start > 0 ? "…" : string.Empty) + text + (end < body.Length ? "…" : string.Empty);
    }

    private static int ParseInt(HttpRequest http, string name, int fallback, int min, int max) =>
        http.Query.TryGetValue(name, out var raw) && int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
}

public sealed record ReindexPayload;

/// <summary>Clears and refills the tenant's index from every <see cref="ISearchSource"/>.</summary>
internal sealed class ReindexOperation(IEnumerable<ISearchSource> sources, ISearchIndex index) : OperationHandler<ReindexPayload>
{
    public const string OperationType = "search.reindex";

    public override string Type => OperationType;

    protected override async Task<object?> ExecuteAsync(ReindexPayload payload, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var all = sources.ToList();
        for (var i = 0; i < all.Count; i++)
        {
            await index.DeleteSourceAsync(all[i].SourceType, cancellationToken);
            await all[i].ReindexAsync(index, cancellationToken);
            await progress.ReportAsync((i + 1) * 100 / all.Count, cancellationToken);
        }

        return new { sources = all.Select(s => s.SourceType).ToList() };
    }
}
