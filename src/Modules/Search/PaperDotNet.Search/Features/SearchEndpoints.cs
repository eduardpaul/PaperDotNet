using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Search.Contracts;

namespace PaperDotNet.Search.Features;

public sealed record ReindexResponse(Guid Id, string Status);

public sealed record ReindexPayload;

public sealed record ReindexResult(IReadOnlyList<string> Sources);

/// <summary>Unified search (SRC-01…04, SRC-09): full text with filters and facets, only what the caller may read.</summary>
internal static class SearchEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/search").WithTags("Search");
        group.MapGet("", SearchAsync).RequireScope(SearchScopes.Read).WithName("Search")
            .WithDescription("q: words, \"phrases\", OR, -exclude, prefix* (optional when filtering). Filters: workspaceId, containerId (list), "
                + "contentTypeId, termId (includes child terms), createdBy, updatedFrom/updatedTo. Paging with $top/$skip.");
        group.MapPost("/reindex", ReindexAsync).RequireScope(SearchScopes.Manage).WithName("ReindexSearch")
            .WithDescription("Rebuilds the organization's index from every source, in the background: 202 with the operation to poll.");
    }

    private static async Task<Results<Ok<SearchResponse>, ValidationProblem>> SearchAsync(
        string? q, string? mode, Guid? workspaceId, Guid? containerId, Guid? contentTypeId, Guid? termId, Guid? createdBy,
        DateTimeOffset? updatedFrom, DateTimeOffset? updatedTo, HttpRequest http, Caller caller, SearchService search, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(mode) && !string.Equals(mode, SearchService.KeywordMatch, StringComparison.OrdinalIgnoreCase))
        {
            return ApiErrors.Validation("mode", mode is "semantic" or "hybrid"
                ? "Semantic search is not configured on this server (AI:Embeddings)."
                : "Use keyword, semantic or hybrid.");
        }

        var top = ParseInt(http, "$top", SearchService.DefaultTop, 1, SearchService.MaxTop);
        var skip = ParseInt(http, "$skip", 0, 0, int.MaxValue);
        var (result, parameter, error) = await search.SearchAsync(
            caller.TenantId, caller.UserId, new SearchRequest(q, workspaceId, containerId, contentTypeId, termId, createdBy, updatedFrom, updatedTo, top, skip), ct);
        if (result is null)
        {
            return ApiErrors.Validation(parameter!, error!);
        }

        string? nextLink = null;
        if (skip + top < result.Count)
        {
            var query = http.Query
                .Where(p => p.Key is not "$skip")
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value.ToString())}")
                .Append($"$skip={(skip + top).ToString(CultureInfo.InvariantCulture)}");
            nextLink = $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', query)}";
        }

        return TypedResults.Ok(new SearchResponse(result.Hits, result.Count, result.Facets!, nextLink, SearchService.KeywordMatch));
    }

    private static async Task<Accepted<ReindexResponse>> ReindexAsync(Caller caller, IOperations operations, CancellationToken ct)
    {
        var id = await operations.StartAsync(caller.Actor, ReindexOperation.OperationType, new ReindexPayload(), SearchJson.Default.ReindexPayload, ct);
        return TypedResults.Accepted($"/v1.0/operations/{id}", new ReindexResponse(id, "notStarted"));
    }

    private static int ParseInt(HttpRequest http, string name, int fallback, int min, int max) =>
        http.Query.TryGetValue(name, out var raw) && int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
}

/// <summary>Clears and refills a tenant's index from every <see cref="ISearchSource"/> (SRC-10).</summary>
public sealed class SearchReindexer(IEnumerable<ISearchSource> sources, ISearchIndex index)
{
    /// <summary>Rebuilds the index; <paramref name="progress"/> receives 0–100. Returns the source types.</summary>
    public async Task<IReadOnlyList<string>> ReindexAsync(Guid tenantId, Func<int, Task> progress, CancellationToken cancellationToken)
    {
        var all = sources.ToList();
        for (var i = 0; i < all.Count; i++)
        {
            var done = i;
            await index.DeleteSourceAsync(tenantId, all[i].SourceType, cancellationToken);
            await all[i].ReindexAsync(tenantId, index, fraction => progress((int)((done + fraction) * 100 / all.Count)), cancellationToken);
        }

        await progress(100);
        return [.. all.Select(s => s.SourceType)];
    }
}

/// <summary>The reindex operation (<c>POST /v1.0/search/reindex</c>), with progress.</summary>
internal sealed class ReindexOperation(SearchReindexer reindexer) : OperationHandler<ReindexPayload>
{
    public const string OperationType = "search.reindex";

    public override string Type => OperationType;

    protected override JsonTypeInfo<ReindexPayload> PayloadJson => SearchJson.Default.ReindexPayload;

    protected override async Task<JsonNode?> ExecuteAsync(ReindexPayload payload, OperationContext context, CancellationToken cancellationToken)
    {
        var sources = await reindexer.ReindexAsync(context.Actor.TenantId, percent => context.Progress.ReportAsync(percent, cancellationToken), cancellationToken);
        return System.Text.Json.JsonSerializer.SerializeToNode(new ReindexResult(sources), SearchJson.Default.ReindexResult);
    }
}
