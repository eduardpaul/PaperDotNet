using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
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
    [property: JsonPropertyName("@removed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeltaRemoved? Removed)
{
    internal static DeltaItem From(ListItem item)
    {
        var response = ItemResponse.From(item);
        return new(item.Id, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, response.Fields, null);
    }

    /// <summary>
    /// The item left the caller's view: <c>deleted</c>, or <c>changed</c> when it still exists but the caller can no
    /// longer read it.
    /// </summary>
    internal static DeltaItem Gone(Guid id, string reason) => new(id, null, null, null, null, null, null, null, null, null, new(reason));
}

public sealed record DeltaRemoved(string Reason);

public sealed record DeltaPage(
    [property: JsonPropertyName("value")] IReadOnlyList<DeltaItem> Value,
    [property: JsonPropertyName("@odata.nextLink"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextLink,
    [property: JsonPropertyName("@odata.deltaLink"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeltaLink);

/// <summary>
/// Delta sync of a list (API-05). The first call returns all visible items (paged); the last page carries a
/// <c>deltaLink</c>. Calling it returns only what changed since: changed items, and items that were deleted or that
/// the caller can no longer read as <c>{ id, @removed }</c>. Permission changes return the affected items (ADR-0035);
/// 410 means: start over without a token.
/// </summary>
internal static class DeltaEndpoints
{
    private const int DefaultTop = 200;
    private const int MaxTop = 1000;

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/items").WithTags("Items")
            .MapGet("/delta", DeltaAsync)
            .RequireScope(ListScopes.Read)
            .WithName("ItemsDelta")
            .WithDescription("Without a token: all items, paged, then a deltaLink. With $deltatoken: the changes since.");

    private static async Task<Results<Ok<DeltaPage>, ValidationProblem, ProblemHttpResult>> DeltaAsync(
        Guid workspaceId, Guid listId, [FromQuery(Name = "$top")] string? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        [FromQuery(Name = "$deltatoken")] string? deltaToken, Caller caller, ListSchemaLoader loader, IItemQueries queries, ListsDbContext db,
        TimeProvider time, IOptions<ListsOptions> options, HttpRequest request, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        var schema = await loader.LoadAsync(listCaller, workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        var take = DefaultTop;
        if (top is not null && (!int.TryParse(top, NumberStyles.None, CultureInfo.InvariantCulture, out take) || take is < 1 or > MaxTop))
        {
            return ApiErrors.Validation("$top", $"Use a number from 1 to {MaxTop}.");
        }

        var now = time.GetUtcNow();
        var cutoff = now - options.Value.DeltaSafetyWindow;
        DeltaToken token;
        if ((skipToken ?? deltaToken) is not { } raw)
        {
            token = new DeltaToken(await BaselineAsync(db, caller.TenantId, listId, cutoff, cancellationToken), cutoff, Initial: true, After: null);
        }
        else if (DeltaToken.TryDecode(raw) is { } decoded)
        {
            token = decoded;
        }
        else
        {
            return ApiErrors.Validation("token", "The token is not valid.");
        }

        if (token.Issued < now - TimeSpan.FromDays(options.Value.DeltaRetentionDays))
        {
            return ResyncRequired("The token has expired.");
        }

        return token.Initial
            ? await InitialAsync(listCaller, schema, queries, token, take, request, cancellationToken)
            : await ChangesAsync(caller.TenantId, schema, db, token, cutoff, take, options.Value.DeltaScopeLimit, request, cancellationToken);
    }

    /// <summary>The last change before the cutoff: the initial sync returns everything up to there.</summary>
    private static async Task<long> BaselineAsync(ListsDbContext database, Guid tenantId, Guid listId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var list = listId;
        var until = cutoff.ToUnixTimeMilliseconds();
        var ct = cancellationToken;
        return await context.ItemChanges.Where(c => c.TenantId == tenant && c.ListId == list && c.At <= until).MaxAsync(c => (long?)c.Sequence, ct) ?? 0;
    }

    private static async Task<Results<Ok<DeltaPage>, ValidationProblem, ProblemHttpResult>> InitialAsync(
        ListCaller caller, ListSchema schema, IItemQueries queries, DeltaToken token, int take, HttpRequest request, CancellationToken cancellationToken)
    {
        var query = new ItemQuery(
            caller.TenantId, [schema.List.Id], ParsedItemQuery.Empty, schema.Access.Scopes(WorkspaceAccessLevel.Read), FolderMode.All, null,
            new ItemCursor(token.After, 0), take, false);
        var result = await queries.QueryAsync(query, cancellationToken);
        var page = result.Items.Select(DeltaItem.From).ToList();
        return TypedResults.Ok(result.HasMore
            ? new DeltaPage(page, Link(request, "$skiptoken", token with { After = result.Items[^1].Id }), null)
            : new DeltaPage(page, null, Link(request, "$deltatoken", token with { Initial = false, After = null })));
    }

    private static async Task<Results<Ok<DeltaPage>, ValidationProblem, ProblemHttpResult>> ChangesAsync(
        Guid tenantId, ListSchema schema, ListsDbContext db, DeltaToken token, DateTimeOffset cutoff, int take, int scopeLimit, HttpRequest request,
        CancellationToken cancellationToken)
    {
        var listId = schema.List.Id;
        var rows = await ChangesAfterAsync(db, tenantId, listId, token.Sequence, cutoff, take + 1, cancellationToken);
        var more = rows.Count > take;
        rows = [.. rows.Take(take)];

        // Items in the order of their first change on the page. "Seen" means the caller may have had the item: it could
        // read a scope the item was in, or the item's scope changed its access list (ADR-0035).
        var order = new List<Guid>();
        var seen = new Dictionary<Guid, bool>();
        void Note(Guid id, bool couldRead)
        {
            if (seen.TryGetValue(id, out var before))
            {
                seen[id] = before || couldRead;
            }
            else
            {
                order.Add(id);
                seen[id] = couldRead;
            }
        }

        bool Readable(Guid? scopeId) => scopeId is { } id && schema.Access.Level(id) >= WorkspaceAccessLevel.Read;
        foreach (var change in rows)
        {
            if (change.Kind == ItemChangeKinds.ScopeChanged)
            {
                var inScope = await ScopeItemsAsync(db, tenantId, listId, change.ScopeId!.Value, scopeLimit + 1, cancellationToken);
                if (inScope.Count > scopeLimit)
                {
                    return ResyncRequired("Permissions of many items changed.");
                }

                inScope.ForEach(id => Note(id, couldRead: true));
            }
            else if (change.ItemId is { } itemId)
            {
                Note(itemId, Readable(change.ScopeId ?? listId) || Readable(change.FromScopeId));
            }
        }

        // The item's current row decides: readable items are returned, others removed if the caller may have had them.
        var page = new List<DeltaItem>();
        foreach (var id in order)
        {
            var item = await FindAnyAsync(db, tenantId, id, cancellationToken);
            if (item is null || item.DeletedAt is not null || item.ListId != listId)
            {
                if (seen[id] || Readable(item?.ScopeId))
                {
                    page.Add(DeltaItem.Gone(id, "deleted"));
                }
            }
            else if (Readable(item.ScopeId))
            {
                page.Add(DeltaItem.From(item));
            }
            else if (seen[id])
            {
                page.Add(DeltaItem.Gone(id, "changed"));
            }
        }

        // Everything up to the cutoff was read, unless there are more pages.
        var next = new DeltaToken(
            rows.Count == 0 ? token.Sequence : rows[^1].Sequence,
            more ? DateTimeOffset.FromUnixTimeMilliseconds(rows[^1].At) : cutoff > token.Issued ? cutoff : token.Issued,
            Initial: false,
            After: null);
        return TypedResults.Ok(more
            ? new DeltaPage(page, Link(request, "$skiptoken", next), null)
            : new DeltaPage(page, null, Link(request, "$deltatoken", next)));
    }

    private static Task<List<ItemChange>> ChangesAfterAsync(
        ListsDbContext database, Guid tenantId, Guid listId, long sequence, DateTimeOffset cutoff, int count, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var list = listId;
        var after = sequence;
        var until = cutoff.ToUnixTimeMilliseconds();
        var take = count;
        var ct = cancellationToken;
        return context.ItemChanges.AsNoTracking()
            .Where(c => c.TenantId == tenant && c.ListId == list && c.Sequence > after && c.At <= until)
            .OrderBy(c => c.Sequence)
            .Take(take)
            .ToListAsync(ct);
    }

    private static Task<List<Guid>> ScopeItemsAsync(ListsDbContext database, Guid tenantId, Guid listId, Guid scopeId, int count, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var list = listId;
        var scope = scopeId;
        var take = count;
        var ct = cancellationToken;
        return context.Items.AsNoTracking()
            .Where(i => i.TenantId == tenant && i.ListId == list && i.ScopeId == scope && i.DeletedAt == null)
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .Take(take)
            .ToListAsync(ct);
    }

    /// <summary>An item of the tenant, also in the recycle bin.</summary>
    private static Task<ListItem?> FindAnyAsync(ListsDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        return context.Items.AsNoTracking().Where(i => i.TenantId == tenant && i.Id == id).FirstOrDefaultAsync(ct);
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
/// Position in the change log. <see cref="Issued"/> is the time up to which changes were read; tokens older than the
/// log's retention expire. Initial tokens also carry the paging position.
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

/// <summary>Removes change-log entries older than the delta retention (their tokens expire), daily at 03:45 UTC.</summary>
internal sealed class ItemChangeCleanupJob(ListsDbContext db, TimeProvider time, IOptions<ListsOptions> options) : ITenantRecurringJob
{
    public const string Name = "lists.change-log-cleanup";
    public const string Schedule = "45 3 * * *";
    private const int BatchSize = 1000;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var before = (time.GetUtcNow() - TimeSpan.FromDays(options.Value.DeltaRetentionDays)).ToUnixTimeMilliseconds();
        var take = BatchSize;
        var ct = cancellationToken;
        while (true)
        {
            var expired = await context.ItemChanges.Where(c => c.TenantId == tenant && c.At < before).OrderBy(c => c.Sequence).Take(take).ToListAsync(ct);
            if (expired.Count == 0)
            {
                return;
            }

            context.ItemChanges.RemoveRange(expired);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }
    }
}
