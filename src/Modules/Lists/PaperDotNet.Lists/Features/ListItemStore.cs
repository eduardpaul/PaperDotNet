using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Messaging;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// Reads and writes list items for the API and for other modules (<see cref="IListItemStore"/>). Queries copy their
/// arguments into locals (precompiled queries, ADR-0039).
/// </summary>
internal sealed class ListItemStore(ListsDbContext db, IOutbox outbox, IItemQueries queries) : IListItemStore
{
    public static IReadOnlyList<FieldDefinition> FieldsOf(ListDefinition list) =>
        JsonSerializer.Deserialize(list.Fields, ListsJson.Default.IReadOnlyListFieldDefinition) ?? [];

    public static ListItemData ToData(ListItem item, IReadOnlyList<FieldDefinition> fields) =>
        new(item.Id, item.ListId, FieldValues.ForApi(fields, item.Title, item.Fields), item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, item.Version);

    public Task<ListDefinition?> FindListEntityAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var id = listId;
        var ct = cancellationToken;
        return context.Lists.Where(l => l.TenantId == tenant && l.Id == id).FirstOrDefaultAsync(ct);
    }

    private Task<ListItem?> FindItemAsync(Guid tenantId, Guid listId, Guid itemId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var id = itemId;
        var ct = cancellationToken;
        return context.Items.Where(i => i.TenantId == tenant && i.ListId == list && i.Id == id).FirstOrDefaultAsync(ct);
    }

    public async Task<ListData?> FindListAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken) =>
        await FindListEntityAsync(tenantId, listId, cancellationToken) is { } list ? new ListData(list.Id, list.Name) : null;

    public async Task<ListData?> FindListByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var listName = name;
        var ct = cancellationToken;
        return await context.Lists.Where(l => l.TenantId == tenant && l.Name == listName).FirstOrDefaultAsync(ct) is { } list ? new ListData(list.Id, list.Name) : null;
    }

    public async Task<ListItemData?> GetAsync(Guid tenantId, Guid listId, Guid itemId, CancellationToken cancellationToken) =>
        await FindListEntityAsync(tenantId, listId, cancellationToken) is { } list && await FindItemAsync(tenantId, listId, itemId, cancellationToken) is { } item
            ? ToData(item, FieldsOf(list))
            : null;

    public async Task<(IReadOnlyList<ListItemData> Items, string? Error)> QueryAsync(
        Guid tenantId, Guid listId, string? filter, string? orderBy, int top, Guid? itemId, CancellationToken cancellationToken)
    {
        if (await FindListEntityAsync(tenantId, listId, cancellationToken) is not { } list)
        {
            return ([], "The list does not exist.");
        }

        var fields = FieldsOf(list);
        var (filterClause, orderByClause, error) = ItemQueryParser.Parse(filter, orderBy, fields);
        if (error is not null)
        {
            return ([], error);
        }

        try
        {
            var found = await queries.QueryAsync(new ItemQuery(tenantId, listId, filterClause, orderByClause, new PageRequest(top, null, 0), false, itemId), cancellationToken);
            return ([.. found.Items.Take(top).Select(i => ToData(i, fields))], null);
        }
        catch (NotSupportedException exception)
        {
            return ([], exception.Message);
        }
    }

    public async Task<ItemWriteResult> CreateAsync(ChangeActor actor, Guid listId, Guid? itemId, JsonObject values, CancellationToken cancellationToken)
    {
        if (await FindListEntityAsync(actor.TenantId, listId, cancellationToken) is not { } list)
        {
            return new(ItemWriteStatus.NotFound);
        }

        var fields = FieldsOf(list);
        if (itemId is { } requested && await FindItemAsync(actor.TenantId, listId, requested, cancellationToken) is { } existing)
        {
            return new(ItemWriteStatus.Exists, ToData(existing, fields));
        }

        var stored = new JsonObject();
        var title = "";
        if (FieldValues.Merge(fields, values, stored, ref title) is { Count: > 0 } errors)
        {
            return new(ItemWriteStatus.Invalid, Errors: errors);
        }

        var item = new ListItem { Id = itemId ?? Ids.New(), TenantId = actor.TenantId, ListId = list.Id, Title = title, Fields = stored.ToJsonString(), CreatedBy = actor.UserId };
        db.Items.Add(item);
        await outbox.SaveChangesAsync(db, [new ItemCreated(list.Id, item.Id, title) { TenantId = actor.TenantId, UserId = actor.UserId, Depth = actor.Depth }], cancellationToken);
        return new(ItemWriteStatus.Ok, ToData(item, fields));
    }

    public async Task<ItemWriteResult> UpdateAsync(ChangeActor actor, Guid listId, Guid itemId, JsonObject values, uint? ifMatch, CancellationToken cancellationToken)
    {
        if (await FindListEntityAsync(actor.TenantId, listId, cancellationToken) is not { } list
            || await FindItemAsync(actor.TenantId, listId, itemId, cancellationToken) is not { } item)
        {
            return new(ItemWriteStatus.NotFound);
        }

        if (ifMatch is { } version && item.Version != version)
        {
            return new(ItemWriteStatus.PreconditionFailed);
        }

        var fields = FieldsOf(list);
        var stored = JsonNode.Parse(item.Fields)?.AsObject() ?? [];
        var title = item.Title;
        if (FieldValues.Merge(fields, values, stored, ref title) is { Count: > 0 } errors)
        {
            return new(ItemWriteStatus.Invalid, Errors: errors);
        }

        item.Title = title;
        item.Fields = stored.ToJsonString();
        item.UpdatedBy = actor.UserId;
        try
        {
            await outbox.SaveChangesAsync(db, [new ItemUpdated(list.Id, item.Id, title, [.. values.Select(p => p.Key)]) { TenantId = actor.TenantId, UserId = actor.UserId, Depth = actor.Depth }], cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(item).State = EntityState.Detached;
            return new(ItemWriteStatus.PreconditionFailed);
        }

        return new(ItemWriteStatus.Ok, ToData(item, fields));
    }

    public async Task<ItemWriteResult> DeleteAsync(ChangeActor actor, Guid listId, Guid itemId, uint? ifMatch, CancellationToken cancellationToken)
    {
        if (await FindItemAsync(actor.TenantId, listId, itemId, cancellationToken) is not { } item)
        {
            return new(ItemWriteStatus.NotFound);
        }

        if (ifMatch is { } version && item.Version != version)
        {
            return new(ItemWriteStatus.PreconditionFailed);
        }

        db.Items.Remove(item);
        await outbox.SaveChangesAsync(db, [new ItemDeleted(listId, item.Id, item.Title) { TenantId = actor.TenantId, UserId = actor.UserId, Depth = actor.Depth }], cancellationToken);
        return new(ItemWriteStatus.Ok);
    }
}
