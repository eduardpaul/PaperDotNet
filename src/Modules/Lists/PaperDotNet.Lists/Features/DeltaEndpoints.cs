using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>An entry of a delta page: an item, or only its id with <c>@removed</c>.</summary>
public sealed record DeltaItem(
    Guid Id,
    Guid? ListId,
    Guid? ContentTypeId,
    Guid? ParentId,
    bool? IsFolder,
    DateTimeOffset? CreatedAt,
    Guid? CreatedBy,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy,
    JsonObject? Fields,
    [property: JsonPropertyName("@removed")] DeltaRemoved? Removed)
{
    internal static DeltaItem From(ItemResponse item) => new(
        item.Id, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.CreatedAt, item.CreatedBy,
        item.UpdatedAt, item.UpdatedBy, item.Fields, null);

    internal static DeltaItem Deleted(Guid id) => new(id, null, null, null, null, null, null, null, null, null, new("deleted"));
}

public sealed record DeltaRemoved(string Reason);

public sealed record DeltaPage(
    [property: JsonPropertyName("value")] IReadOnlyList<DeltaItem> Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink,
    [property: JsonPropertyName("@odata.deltaLink")] string? DeltaLink);

/// <summary>
/// Delta sync of a list (API-05). The first call returns all visible items (paged); the last
/// page carries a <c>deltaLink</c>. Calling it returns only what changed since: changed items,
/// and deleted ones as <c>{ id, @removed }</c>. 410 means: start over without a token.
/// </summary>
internal static class DeltaEndpoints
{
    private const int DefaultTop = 200;
    private const int MaxTop = 1000;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/items", "Items");
        group.MapGet("/delta", DeltaAsync).RequireScope(ListScopes.Read).WithName("ItemsDelta").WithQueryOptions(QueryOptions.Delta);
    }

    private static async Task<Results<Ok<DeltaPage>, ValidationProblem, ProblemHttpResult>> DeltaAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, TimeProvider time, IOptions<ListsOptions> options,
        HttpRequest http, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        var top = DefaultTop;
        if (http.Query.TryGetValue("$top", out var topValue)
            && (!int.TryParse(topValue, NumberStyles.None, CultureInfo.InvariantCulture, out top) || top is < 1 or > MaxTop))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["$top"] = [$"Use a number from 1 to {MaxTop}."] });
        }

        var now = time.GetUtcNow();
        var cutoff = now - options.Value.DeltaSafetyWindow;
        var raw = http.Query.TryGetValue("$skiptoken", out var skip) ? skip.ToString()
            : http.Query.TryGetValue("$deltatoken", out var delta) ? delta.ToString()
            : null;
        DeltaToken token;
        if (raw is null)
        {
            var baseline = await db.ItemChanges.Where(c => c.ListId == listId && c.At <= cutoff).MaxAsync(c => (long?)c.Sequence, ct) ?? 0;
            token = new DeltaToken(baseline, cutoff, Initial: true, After: null);
        }
        else if (DeltaToken.TryDecode(raw) is { } decoded)
        {
            token = decoded;
        }
        else
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["token"] = ["The token is not valid."] });
        }

        if (token.Issued < now - TimeSpan.FromDays(options.Value.DeltaRetentionDays))
        {
            return ResyncRequired("The token has expired.");
        }

        return token.Initial
            ? await InitialAsync(schema, db, token, top, http, ct)
            : await ChangesAsync(schema, db, token, cutoff, top, http, ct);
    }

    private static async Task<Results<Ok<DeltaPage>, ValidationProblem, ProblemHttpResult>> InitialAsync(
        ListSchema schema, ListsDbContext db, DeltaToken token, int top, HttpRequest http, CancellationToken ct)
    {
        var query = db.Items.AsNoTracking().Where(i => i.ListId == schema.List.Id);
        if (schema.Access.Filter(WorkspaceAccessLevel.Read) is { } filter)
        {
            query = query.Where(filter);
        }

        if (token.After is { } after)
        {
            query = query.Where(i => i.Id.CompareTo(after) > 0);
        }

        var items = await query.OrderBy(i => i.Id).Take(top + 1).ToListAsync(ct);
        var more = items.Count > top;
        var page = items.Take(top).Select(i => DeltaItem.From(ItemResponse.From(i))).ToList();
        return TypedResults.Ok(more
            ? new DeltaPage(page, Link(http, "$skiptoken", token with { After = items[top - 1].Id }), null)
            : new DeltaPage(page, null, Link(http, "$deltatoken", token with { Initial = false, After = null })));
    }

    private static async Task<Results<Ok<DeltaPage>, ValidationProblem, ProblemHttpResult>> ChangesAsync(
        ListSchema schema, ListsDbContext db, DeltaToken token, DateTimeOffset cutoff, int top, HttpRequest http, CancellationToken ct)
    {
        var listId = schema.List.Id;
        var changes = db.ItemChanges.AsNoTracking().Where(c => c.ListId == listId && c.Sequence > token.Sequence && c.At <= cutoff);
        if (await changes.AnyAsync(c => c.Kind == ItemChangeKind.Reset, ct))
        {
            return ResyncRequired("Permissions of the list changed.");
        }

        var rows = await changes.OrderBy(c => c.Sequence).Take(top + 1).ToListAsync(ct);
        var more = rows.Count > top;
        rows = rows.Take(top).ToList();

        // The latest change of each item decides; the item's current row wins over the log.
        var latest = rows.Where(c => c.ItemId is not null).GroupBy(c => c.ItemId!.Value).Select(g => g.Last()).OrderBy(c => c.Sequence).ToList();
        var ids = latest.Select(c => c.ItemId!.Value).ToList();
        var items = await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking()
            .Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var page = new List<DeltaItem>();
        foreach (var change in latest)
        {
            var item = items.GetValueOrDefault(change.ItemId!.Value);
            if (schema.Access.Level(item is null ? change.ScopeId : item.ScopeId) < WorkspaceAccessLevel.Read)
            {
                continue;
            }

            page.Add(item is null || item.DeletedAt is not null || item.ListId != listId
                ? DeltaItem.Deleted(change.ItemId!.Value)
                : DeltaItem.From(ItemResponse.From(item)));
        }

        // Everything up to the cutoff was read, unless there are more pages.
        var next = new DeltaToken(
            rows.Count == 0 ? token.Sequence : rows[^1].Sequence,
            more ? rows[^1].At : cutoff > token.Issued ? cutoff : token.Issued,
            Initial: false,
            After: null);

        return TypedResults.Ok(more
            ? new DeltaPage(page, Link(http, "$skiptoken", next), null)
            : new DeltaPage(page, null, Link(http, "$deltatoken", next)));
    }

    private static ProblemHttpResult ResyncRequired(string detail) =>
        ApiErrors.Problem(StatusCodes.Status410Gone, "resyncRequired", detail + " Call delta without a token to sync again.");

    private static string Link(HttpRequest request, string name, DeltaToken token)
    {
        var query = request.Query
            .Where(q => q.Key is not ("$skiptoken" or "$deltatoken"))
            .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}")
            .Append($"{name}={token.Encode()}");
        return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}?{string.Join('&', query)}";
    }
}

/// <summary>
/// Position in the change log. <see cref="Issued"/> is the time up to which changes were read;
/// tokens older than the log's retention expire. Initial tokens also carry the paging position.
/// </summary>
internal sealed record DeltaToken(long Sequence, DateTimeOffset Issued, bool Initial, Guid? After)
{
    public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Join('.',
        Initial ? "i" : "d",
        Sequence.ToString(CultureInfo.InvariantCulture),
        Issued.UtcTicks.ToString(CultureInfo.InvariantCulture),
        After?.ToString("N") ?? "")));

    public static DeltaToken? TryDecode(string value)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)).Split('.');
            if (parts.Length != 4 || parts[0] is not ("i" or "d")
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return null;
            }

            Guid? after = parts[3].Length == 0 ? null : Guid.TryParseExact(parts[3], "N", out var id) ? id : null;
            if (parts[3].Length > 0 && after is null)
            {
                return null;
            }

            return new DeltaToken(sequence, new DateTimeOffset(ticks, TimeSpan.Zero), parts[0] == "i", after);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Removes change-log entries older than the delta retention (their tokens expire).</summary>
internal sealed class ItemChangeCleanupJob(ListsDbContext db, TimeProvider time, IOptions<ListsOptions> options) : Jobs.Contracts.ITenantRecurringJob
{
    public const string Name = "lists.change-log-cleanup";
    public const string Schedule = "45 3 * * *";

    public Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(options.Value.DeltaRetentionDays);
        return db.ItemChanges.Where(c => c.At < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
