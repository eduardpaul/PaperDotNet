using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>A version of an item; <c>fields</c> holds the values after that change (including <c>title</c>).</summary>
public sealed record ItemVersionResponse(
    Guid Id, int Number, bool IsCurrent, Guid ContentTypeId, IReadOnlyList<string> ChangedFields, DateTimeOffset CreatedAt, Guid? CreatedBy, JsonObject Fields);

public sealed record RecycleBinItemResponse(ItemResponse Item, DateTimeOffset DeletedAt, Guid? DeletedBy);

/// <summary>Item version history (LST-11/12) and the list recycle bin (LST-13).</summary>
internal static class ItemHistoryEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var items = endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/items/{{itemId:guid}}/versions", "Items");
        items.MapGet("", ListVersionsAsync).RequireScope(ListScopes.Read).WithName("ListItemVersions");
        items.MapGet("/{number:int}", GetVersionAsync).RequireScope(ListScopes.Read).WithName("GetItemVersion");
        items.MapPost("/{number:int}/restore", RestoreVersionAsync).RequireScope(ListScopes.Write).WithName("RestoreItemVersion");

        var bin = endpoints.MapV1Group($"{ListEndpoints.Route}/{{listId:guid}}/recycleBin", "Recycle bin");
        bin.MapGet("", ListDeletedAsync).RequireScope(ListScopes.Read).WithName("ListRecycleBin");
        bin.MapPost("/{itemId:guid}/restore", RestoreDeletedAsync).RequireScope(ListScopes.Write).WithName("RestoreRecycleBinItem");
        bin.MapDelete("/{itemId:guid}", PurgeAsync).RequireScope(ListScopes.Write).WithName("PurgeRecycleBinItem");
    }

    // ---- Versions -----------------------------------------------------------

    /// <summary>Newest first.</summary>
    private static async Task<Results<Ok<Page<ItemVersionResponse>>, ProblemHttpResult>> ListVersionsAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, HttpRequest http, CancellationToken ct)
    {
        if (await LoadItemAsync(workspaceId, listId, itemId, loader, db, ct) is not { } item)
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.From(http);
        var versions = await db.ItemVersions.AsNoTracking()
            .Where(v => v.ItemId == itemId)
            .Where(v => page.After == null || v.Id.CompareTo(page.After.Value) < 0)
            .OrderByDescending(v => v.Id)
            .Take(page.Top + 1)
            .ToListAsync(ct);
        var current = await CurrentNumberAsync(db, itemId, ct);
        return TypedResults.Ok(Page.Create(versions.Select(v => ToResponse(v, current)).ToList(), page, http, v => v.Id));
    }

    private static async Task<Results<Ok<ItemVersionResponse>, ProblemHttpResult>> GetVersionAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
    {
        if (await LoadItemAsync(workspaceId, listId, itemId, loader, db, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var version = await db.ItemVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && v.Number == number, ct);
        return version is null
            ? ApiErrors.NotFound("The version was not found.")
            : TypedResults.Ok(ToResponse(version, await CurrentNumberAsync(db, itemId, ct)));
    }

    /// <summary>
    /// Makes an old version current again by saving its values as a new change
    /// (validated like any update, so mutators and events run). Requires <c>If-Match</c> of the item.
    /// </summary>
    private static async Task<Results<Ok<ItemResponse>, ValidationProblem, ProblemHttpResult>> RestoreVersionAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        var (schema, item, problem) = await ItemEndpoints.LoadForChangeAsync(workspaceId, listId, itemId, loader, db, http, ct);
        if (problem is not null)
        {
            return problem;
        }

        var version = await db.ItemVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && v.Number == number, ct);
        if (version is null)
        {
            return ApiErrors.NotFound("The version was not found.");
        }

        // Old values, plus null for values of the version's content type that it did not have.
        var patch = JsonNode.Parse(version.Fields)!.AsObject();
        var defined = schema!.FindContentType(version.ContentTypeId)?.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var key in JsonNode.Parse(item!.Fields)!.AsObject().Select(p => p.Key).Where(k => !patch.ContainsKey(k) && defined.Contains(k)).ToList())
        {
            patch[key] = null;
        }

        patch["title"] = version.Title;
        using var document = JsonDocument.Parse(patch.ToJsonString());
        try
        {
            var result = await writer.UpdateAsync(schema, item, version.ContentTypeId, Optional<Guid?>.None, document.RootElement, ct);
            if (result.Forbidden)
            {
                return ListEndpoints.Forbidden();
            }

            if (result.Errors is not null)
            {
                return ApiErrors.Validation(result.Errors);
            }

            if (result.Cancelled is not null)
            {
                return ItemEndpoints.CancelledByMutator(result.Cancelled);
            }

            ETags.Set(response, result.Item!.Version);
            return TypedResults.Ok(ItemResponse.From(result.Item));
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }
    }

    // ---- Recycle bin --------------------------------------------------------

    private static async Task<Results<Ok<Page<RecycleBinItemResponse>>, ProblemHttpResult>> ListDeletedAsync(
        Guid workspaceId, Guid listId, ListSchemaLoader loader, ListsDbContext db, HttpRequest http, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        if (schema is null)
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.From(http);
        var deleted = Deleted(db).AsNoTracking();
        if (schema.Access.Filter(WorkspaceAccessLevel.Contribute) is { } writable)
        {
            deleted = deleted.Where(writable);
        }

        var items = await deleted
            .Where(i => i.ListId == listId)
            .Where(i => page.After == null || i.Id.CompareTo(page.After.Value) > 0)
            .OrderBy(i => i.Id)
            .Take(page.Top + 1)
            .ToListAsync(ct);
        var value = items.Select(i => new RecycleBinItemResponse(ItemResponse.From(i), i.DeletedAt!.Value, i.DeletedBy)).ToList();
        return TypedResults.Ok(Page.Create(value, page, http, r => r.Item.Id));
    }

    private static async Task<Results<Ok<ItemResponse>, ProblemHttpResult>> RestoreDeletedAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer,
        HttpResponse response, CancellationToken ct)
    {
        var (schema, item, problem) = await LoadDeletedAsync(workspaceId, listId, itemId, WorkspaceAccessLevel.Contribute, loader, db, ct);
        if (problem is not null)
        {
            return problem;
        }

        await writer.RestoreAsync(schema!, item!, ct);
        ETags.Set(response, item!.Version);
        return TypedResults.Ok(ItemResponse.From(item));
    }

    /// <summary>Deletes an item in the recycle bin permanently (list managers only).</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> PurgeAsync(
        Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer, CancellationToken ct)
    {
        var (_, item, problem) = await LoadDeletedAsync(workspaceId, listId, itemId, WorkspaceAccessLevel.Manage, loader, db, ct);
        if (problem is not null)
        {
            return problem;
        }

        await writer.PurgeAsync([item!], ct);
        return TypedResults.NoContent();
    }

    // ---- Helpers ------------------------------------------------------------

    internal static IQueryable<ListItem> Deleted(ListsDbContext db) =>
        db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(i => i.DeletedAt != null);

    private static async Task<(ListSchema? Schema, ListItem? Item, ProblemHttpResult? Problem)> LoadDeletedAsync(
        Guid workspaceId, Guid listId, Guid itemId, WorkspaceAccessLevel required, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        var item = schema is null ? null : await Deleted(db).FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);
        if (item is null)
        {
            return (null, null, ApiErrors.NotFound());
        }

        var level = schema!.Access.Level(item.ScopeId);
        if (level < WorkspaceAccessLevel.Contribute)
        {
            return (null, null, ApiErrors.NotFound());
        }

        return level < required ? (null, null, ListEndpoints.Forbidden()) : (schema, item, null);
    }

    private static async Task<ListItem?> LoadItemAsync(Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ListsDbContext db, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(workspaceId, listId, ct);
        var item = schema is null ? null : await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);
        return item is not null && schema!.Access.Level(item.ScopeId) >= WorkspaceAccessLevel.Read ? item : null;
    }

    private static async Task<int> CurrentNumberAsync(ListsDbContext db, Guid itemId, CancellationToken ct) =>
        await db.ItemVersions.Where(v => v.ItemId == itemId).MaxAsync(v => (int?)v.Number, ct) ?? 0;

    private static ItemVersionResponse ToResponse(ItemVersion v, int current)
    {
        var fields = new JsonObject { ["title"] = v.Title };
        foreach (var (key, value) in JsonNode.Parse(v.Fields)!.AsObject().ToList())
        {
            fields[key] = value?.DeepClone();
        }

        return new ItemVersionResponse(v.Id, v.Number, v.Number == current, v.ContentTypeId, v.ChangedFields, v.CreatedAt, v.CreatedBy, fields);
    }
}

/// <summary>Permanently deletes recycle-bin items older than the retention period (daily).</summary>
internal sealed class RecycleBinCleanupJob(ListsDbContext db, ItemWriter writer, TimeProvider time, Microsoft.Extensions.Options.IOptions<ListsOptions> options)
    : Jobs.Contracts.ITenantRecurringJob
{
    public const string Name = "lists.recycle-bin-cleanup";
    public const string Schedule = "30 3 * * *";
    private const int BatchSize = 200;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(options.Value.RecycleBinRetentionDays);
        while (true)
        {
            var expired = await ItemHistoryEndpoints.Deleted(db).Where(i => i.DeletedAt < cutoff).OrderBy(i => i.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (expired.Count == 0)
            {
                return;
            }

            await writer.PurgeAsync(expired, cancellationToken);
            db.ChangeTracker.Clear();
        }
    }
}

public sealed class ListsOptions
{
    public const string Section = "Lists";

    /// <summary>Days an item stays in the recycle bin before it is deleted permanently (SharePoint default: 93).</summary>
    public int RecycleBinRetentionDays { get; set; } = 93;

    /// <summary>Days changes stay in the delta change log; older delta tokens get 410 (resync).</summary>
    public int DeltaRetentionDays { get; set; } = 30;

    /// <summary>
    /// Delta only returns changes older than this, so changes of transactions that commit late
    /// (with a lower sequence) are not skipped.
    /// </summary>
    public TimeSpan DeltaSafetyWindow { get; set; } = TimeSpan.FromSeconds(2);
}
