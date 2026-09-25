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

/// <summary>
/// A search result. <see cref="Snippet"/> comes from the passage that matched best and <see cref="Page"/> is its page
/// (SRC-09; null when the match is not on a page). <see cref="MatchedBy"/> says how it was found:
/// <c>keyword</c>, <c>semantic</c> or both.
/// </summary>
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
    DateTimeOffset UpdatedAt)
{
    public int? Page { get; init; }

    public IReadOnlyList<string> MatchedBy { get; init; } = [];
}

public sealed record FacetValue(Guid Value, int Count);

public sealed record SearchFacets(
    IReadOnlyList<FacetValue> Workspace, IReadOnlyList<FacetValue> Container, IReadOnlyList<FacetValue> ContentType, IReadOnlyList<FacetValue> Term);

/// <summary>
/// Results; <c>mode</c> is how they were found. In <c>semantic</c> and <c>hybrid</c> mode the count and facets cover the
/// best candidates only (see <c>Search:CandidateLimit</c>).
/// </summary>
public sealed record SearchResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<SearchHit> Value,
    [property: JsonPropertyName("@odata.count")] int Count,
    [property: JsonPropertyName("facets")] SearchFacets Facets,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink,
    [property: JsonPropertyName("mode")] SearchMode Mode);

public sealed record ReindexResponse(Guid Id, OperationStatus Status);

/// <summary>Unified search (SRC-01…04, SRC-07…09): full text and meaning, filters with facets, only what the caller may read.</summary>
internal static class SearchEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("search", "Search");
        group.MapGet("", SearchAsync).RequireScope(SearchScopes.Read).WithName("Search");
        group.MapPost("/reindex", ReindexAsync).RequireScope(SearchScopes.Manage).WithName("ReindexSearch");
    }

    /// <summary>
    /// <c>q</c>: words, <c>"phrases"</c>, <c>OR</c>, <c>-exclude</c>, <c>prefix*</c> (optional when filtering).
    /// <c>mode</c>: <c>keyword</c>, <c>semantic</c> (by meaning) or <c>hybrid</c> (both; the default when semantic search
    /// is configured). Filters: <c>workspaceId</c>, <c>containerId</c> (list), <c>contentTypeId</c>, <c>termId</c> (includes
    /// child terms), <c>createdBy</c>, <c>updatedFrom</c>/<c>updatedTo</c>. Paging with <c>$top</c>/<c>$skip</c>.
    /// </summary>
    private static async Task<Results<Ok<SearchResponse>, ValidationProblem>> SearchAsync(
        string? q, string? mode, Guid? workspaceId, Guid? containerId, Guid? contentTypeId, Guid? termId, Guid? createdBy,
        DateTimeOffset? updatedFrom, DateTimeOffset? updatedTo, HttpRequest http, SearchService search, CancellationToken ct)
    {
        SearchMode? parsedMode = null;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                return ApiErrors.Validation(new Dictionary<string, string[]> { ["mode"] = ["Use keyword, semantic or hybrid."] });
            }

            parsedMode = value;
        }

        var top = ParseInt(http, "$top", SearchService.DefaultTop, 1, SearchService.MaxTop);
        var skip = ParseInt(http, "$skip", 0, 0, int.MaxValue);
        var (result, parameter, error) = await search.SearchAsync(
            new SearchRequest(q, parsedMode, workspaceId, containerId, contentTypeId, termId, createdBy, updatedFrom, updatedTo, top, skip), ct);
        if (result is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { [parameter!] = [error!] });
        }

        string? nextLink = null;
        if (skip + top < result.Count)
        {
            var queryString = http.Query
                .Where(p => p.Key is not "$skip")
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value.ToString())}")
                .Append($"$skip={(skip + top).ToString(CultureInfo.InvariantCulture)}");
            nextLink = $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', queryString)}";
        }

        return TypedResults.Ok(new SearchResponse(result.Hits, result.Count, result.Facets!, nextLink, result.Mode));
    }

    /// <summary>Rebuilds the index of the tenant from every source, as an operation (admins).</summary>
    private static async Task<Accepted<ReindexResponse>> ReindexAsync(IOperations operations, CancellationToken ct)
    {
        var id = await operations.StartAsync(ReindexOperation.OperationType, new ReindexPayload(), ct);
        return TypedResults.Accepted($"{ApiRoutes.V1}/operations/{id}", new ReindexResponse(id, OperationStatus.NotStarted));
    }

    private static int ParseInt(HttpRequest http, string name, int fallback, int min, int max) =>
        http.Query.TryGetValue(name, out var raw) && int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
}

public sealed record ReindexPayload;

/// <summary>
/// Clears and refills the current tenant's index from every <see cref="ISearchSource"/> (SRC-10).
/// Used by the reindex operation and by <c>paperdotnet reindex</c>.
/// </summary>
public sealed class SearchReindexer(IEnumerable<ISearchSource> sources, ISearchIndex index)
{
    /// <summary>Rebuilds the index; <paramref name="progress"/> receives 0–100. Returns the source types.</summary>
    public async Task<IReadOnlyList<string>> ReindexAsync(Func<int, Task> progress, CancellationToken cancellationToken)
    {
        var all = sources.ToList();
        for (var i = 0; i < all.Count; i++)
        {
            var done = i;
            await index.DeleteSourceAsync(all[i].SourceType, cancellationToken);
            await all[i].ReindexAsync(index, fraction => progress((int)((done + fraction) * 100 / all.Count)), cancellationToken);
        }

        await progress(100);
        return all.Select(s => s.SourceType).ToList();
    }
}

/// <summary>The reindex operation (<c>POST /v1.0/search/reindex</c>), with progress.</summary>
internal sealed class ReindexOperation(SearchReindexer reindexer) : OperationHandler<ReindexPayload>
{
    public const string OperationType = "search.reindex";

    public override string Type => OperationType;

    protected override async Task<object?> ExecuteAsync(ReindexPayload payload, IOperationProgress progress, CancellationToken cancellationToken) =>
        new { sources = await reindexer.ReindexAsync(percent => progress.ReportAsync(percent, cancellationToken), cancellationToken) };
}
