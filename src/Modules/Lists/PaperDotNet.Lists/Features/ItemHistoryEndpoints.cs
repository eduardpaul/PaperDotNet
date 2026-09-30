using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>A version of an item; <c>fields</c> holds the values after that change (including <c>title</c>).</summary>
public sealed record ItemVersionResponse(
    Guid Id, int Number, bool IsCurrent, Guid ContentTypeId, IReadOnlyList<string> ChangedFields, DateTimeOffset CreatedAt, Guid? CreatedBy, JsonObject Fields);

public sealed record RecycleBinItemResponse(ItemResponse Item, DateTimeOffset DeletedAt, Guid? DeletedBy);

/// <summary>Item version history (LST-11/12) and the list recycle bin (LST-13).</summary>
internal static class ItemHistoryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var versions = app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/items/{{itemId:guid}}/versions").WithTags("Items");
        versions.MapGet("", ListVersionsAsync).RequireScope(ListScopes.Read).WithName("ListItemVersions")
            .WithDescription("Newest first.");
        versions.MapGet("/{number:int}", GetVersionAsync).RequireScope(ListScopes.Read).WithName("GetItemVersion");
        versions.MapPost("/{number:int}/restore", RestoreVersionAsync).RequireScope(ListScopes.Write).WithName("RestoreItemVersion")
            .WithDescription("Saves the version's values as a new change (validated, mutators and events run). Requires If-Match of the item.");

        var bin = app.MapGroup($"{ListEndpoints.Route}/{{listId:guid}}/recycleBin").WithTags("Recycle bin");
        bin.MapGet("", ListDeletedAsync).RequireScope(ListScopes.Read).WithName("ListRecycleBin");
        bin.MapPost("/{itemId:guid}/restore", RestoreDeletedAsync).RequireScope(ListScopes.Write).WithName("RestoreRecycleBinItem");
        bin.MapDelete("/{itemId:guid}", PurgeAsync).RequireScope(ListScopes.Write).WithName("PurgeRecycleBinItem")
            .WithDescription("Deletes the item permanently (list managers only).");
    }

    // ---- Versions -----------------------------------------------------------

    private static async Task<Results<Ok<Page<ItemVersionResponse>>, ProblemHttpResult>> ListVersionsAsync(
        Guid workspaceId, Guid listId, Guid itemId, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, ListSchemaLoader loader, ItemWriter writer, ListsDbContext db, HttpRequest request, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        if (await LoadItemAsync(listCaller, workspaceId, listId, itemId, loader, writer, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        // Keyset paging on the time-ordered id (the same order as the numbers). Not Skip and Take together: precompiled,
        // both get the parameter name @p, so the offset takes the limit's value (EF Core 11 RC1).
        var page = PageRequest.Create(top, skipToken);
        var context = db;
        var tenant = caller.TenantId;
        var id = itemId;
        var before = page.After ?? Guid.AllBitsSet;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var versions = await context.ItemVersions.AsNoTracking()
            .Where(v => v.TenantId == tenant && v.ItemId == id && v.Id.CompareTo(before) < 0)
            .OrderByDescending(v => v.Id)
            .Take(take)
            .ToListAsync(ct);
        var current = await CurrentNumberAsync(db, tenant, itemId, cancellationToken);
        return TypedResults.Ok(Page.Create(versions.Select(v => ToResponse(v, current)).ToList(), page, request, v => v.Id));
    }

    private static async Task<Results<Ok<ItemVersionResponse>, ProblemHttpResult>> GetVersionAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, Caller caller, ListSchemaLoader loader, ItemWriter writer, ListsDbContext db,
        CancellationToken cancellationToken)
    {
        if (await LoadItemAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, itemId, loader, writer, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var version = await FindVersionAsync(db, caller.TenantId, itemId, number, cancellationToken);
        return version is null
            ? ApiErrors.NotFound("The version was not found.")
            : TypedResults.Ok(ToResponse(version, await CurrentNumberAsync(db, caller.TenantId, itemId, cancellationToken)));
    }

    private static async Task<Results<Ok<ItemResponse>, ValidationProblem, ProblemHttpResult>> RestoreVersionAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, Caller caller, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer,
        HttpRequest request, HttpResponse response, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        var (schema, item, problem) = await ItemEndpoints.LoadForChangeAsync(listCaller, workspaceId, listId, itemId, loader, db, request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var version = await FindVersionAsync(db, caller.TenantId, itemId, number, cancellationToken);
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
            var result = await writer.UpdateAsync(listCaller, schema, item, version.ContentTypeId, Optional<Guid?>.None, document.RootElement, cancellationToken);
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
        Guid workspaceId, Guid listId, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, ListSchemaLoader loader, ListsDbContext db, HttpRequest request, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(ListEndpoints.CallerOf(caller), workspaceId, listId, cancellationToken);
        if (schema is null || schema.Access.ListLevel < WorkspaceAccessLevel.Contribute)
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.Create(top, skipToken);
        var context = db;
        var tenant = caller.TenantId;
        var list = listId;
        var after = page.After ?? Guid.Empty;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var items = await context.Items.AsNoTracking()
            .Where(i => i.TenantId == tenant && i.ListId == list && i.DeletedAt != null && i.Id.CompareTo(after) > 0)
            .OrderBy(i => i.Id)
            .Take(take)
            .ToListAsync(ct);

        // Until item permissions are ported (T08) every item has the list's scope, so this keeps the whole page.
        var value = items
            .Where(i => schema.Access.Level(i.ScopeId) >= WorkspaceAccessLevel.Contribute)
            .Select(i => new RecycleBinItemResponse(ItemResponse.From(i), i.DeletedAt!.Value, i.DeletedBy))
            .ToList();
        return TypedResults.Ok(Page.Create(value, page, request, r => r.Item.Id));
    }

    private static async Task<Results<Ok<ItemResponse>, ProblemHttpResult>> RestoreDeletedAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer,
        HttpResponse response, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        var (schema, item, problem) = await LoadDeletedAsync(listCaller, workspaceId, listId, itemId, WorkspaceAccessLevel.Contribute, loader, db, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await writer.RestoreAsync(listCaller, schema!, item!, cancellationToken);
        ETags.Set(response, item!.Version);
        return TypedResults.Ok(ItemResponse.From(item));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> PurgeAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, ListSchemaLoader loader, ListsDbContext db, ItemWriter writer, CancellationToken cancellationToken)
    {
        var listCaller = ListEndpoints.CallerOf(caller);
        var (schema, item, problem) = await LoadDeletedAsync(listCaller, workspaceId, listId, itemId, WorkspaceAccessLevel.Manage, loader, db, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await writer.PurgeAsync(listCaller, schema!.List.WorkspaceId, [item!], cancellationToken);
        return TypedResults.NoContent();
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>An item of the recycle bin, tracked.</summary>
    internal static Task<ListItem?> FindDeletedAsync(ListsDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        return context.Items.Where(i => i.TenantId == tenant && i.Id == id && i.DeletedAt != null).FirstOrDefaultAsync(ct);
    }

    private static async Task<(ListSchema? Schema, ListItem? Item, ProblemHttpResult? Problem)> LoadDeletedAsync(
        ListCaller caller, Guid workspaceId, Guid listId, Guid itemId, WorkspaceAccessLevel required, ListSchemaLoader loader, ListsDbContext db,
        CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        var item = schema is null ? null : await FindDeletedAsync(db, caller.TenantId, itemId, cancellationToken);
        if (item is null || item.ListId != listId)
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

    private static async Task<ListItem?> LoadItemAsync(
        ListCaller caller, Guid workspaceId, Guid listId, Guid itemId, ListSchemaLoader loader, ItemWriter writer, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        var item = schema is null ? null : await writer.FindAsync(caller.TenantId, listId, itemId, cancellationToken);
        return item is not null && schema!.Access.Level(item.ScopeId) >= WorkspaceAccessLevel.Read ? item : null;
    }

    private static Task<ItemVersion?> FindVersionAsync(ListsDbContext database, Guid tenantId, Guid itemId, int number, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = itemId;
        var n = number;
        var ct = cancellationToken;
        return context.ItemVersions.AsNoTracking().Where(v => v.TenantId == tenant && v.ItemId == id && v.Number == n).FirstOrDefaultAsync(ct);
    }

    private static async Task<int> CurrentNumberAsync(ListsDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        return await context.ItemVersions.Where(v => v.TenantId == tenant && v.ItemId == id).MaxAsync(v => (int?)v.Number, ct) ?? 0;
    }

    private static ItemVersionResponse ToResponse(ItemVersion v, int current)
    {
        var fields = new JsonObject { ["title"] = v.Title };
        foreach (var (key, value) in JsonNode.Parse(v.Fields)!.AsObject().ToList())
        {
            fields[key] = value?.DeepClone();
        }

        var changed = JsonSerializer.Deserialize(v.ChangedFields, ListsJson.Default.IReadOnlyListString) ?? [];
        return new ItemVersionResponse(v.Id, v.Number, v.Number == current, v.ContentTypeId, changed, v.CreatedAt, v.CreatedBy, fields);
    }
}

public sealed class ListsOptions
{
    public const string Section = "Lists";

    /// <summary>Days an item stays in the recycle bin before it is deleted permanently (SharePoint default: 93).</summary>
    public int RecycleBinRetentionDays { get; set; } = 93;

    /// <summary>
    /// Items a request moves to another permission scope itself (breaking or resetting inheritance, moving a
    /// folder); the rest is moved in the background, and keeps its old access until then (ADR-0035).
    /// </summary>
    public int ScopeMoveInlineLimit { get; set; } = 5000;
}

/// <summary>Permanently deletes recycle-bin items older than the retention period (daily at 03:30 UTC).</summary>
internal sealed class RecycleBinCleanupJob(ListsDbContext db, ItemWriter writer, TimeProvider time, IOptions<ListsOptions> options) : ITenantRecurringJob
{
    public const string Name = "lists.recycle-bin-cleanup";
    public const string Schedule = "30 3 * * *";
    private const int BatchSize = 200;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;

        // SQLite cannot compare DateTimeOffset values in SQL: the deleted items are filtered here.
        var deleted = await context.Items.AsNoTracking()
            .Where(i => i.TenantId == tenant && i.DeletedAt != null)
            .Select(i => new DeletedItem(i.Id, i.DeletedAt))
            .ToListAsync(ct);
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(options.Value.RecycleBinRetentionDays);
        var caller = new ListCaller(tenantId, null, System: true);
        foreach (var batch in deleted.Where(d => d.DeletedAt < cutoff).Select(d => d.Id).Chunk(BatchSize))
        {
            var items = new List<ListItem>();
            foreach (var id in batch)
            {
                if (await ItemHistoryEndpoints.FindDeletedAsync(context, tenantId, id, cancellationToken) is { } item)
                {
                    items.Add(item);
                }
            }

            foreach (var byList in items.GroupBy(i => i.ListId))
            {
                await writer.PurgeAsync(caller, await WorkspaceOfAsync(tenantId, byList.Key, cancellationToken), [.. byList], cancellationToken);
            }
        }
    }

    /// <summary>The workspace of a list, also when the list is in the recycle bin.</summary>
    private async Task<Guid> WorkspaceOfAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var ct = cancellationToken;
        return await context.Lists.AsNoTracking().Where(l => l.TenantId == tenant && l.Id == list).Select(l => l.WorkspaceId).FirstOrDefaultAsync(ct);
    }
}
