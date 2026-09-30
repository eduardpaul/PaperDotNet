using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Core.Host.Data;
using PaperDotNet.Core.Messaging;

namespace PaperDotNet.Core.Host.Lists;

/// <summary>Who writes: the tenant, the user (none for the organization, e.g. a workflow) and the causation depth of the change.</summary>
public sealed record ItemActor(Guid TenantId, Guid? UserId, int Depth = 0);

public enum ItemWriteStatus
{
    Ok,
    NotFound,
    Invalid,
    PreconditionFailed,

    /// <summary>An item with the requested id exists already (a repeated create).</summary>
    Exists,
}

/// <summary>The outcome of a write: the item and its list's fields when it succeeded (or existed), else the reason.</summary>
public sealed record ItemWriteResult(ItemWriteStatus Status, ListItem? Item = null, IReadOnlyList<FieldDefinition>? Fields = null, Dictionary<string, string[]>? Errors = null)
{
    public bool Succeeded => Status is ItemWriteStatus.Ok or ItemWriteStatus.Exists;

    public string Describe() => Status switch
    {
        ItemWriteStatus.Invalid => string.Join(" ", Errors?.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}") ?? []),
        ItemWriteStatus.NotFound => "The list or item was not found.",
        ItemWriteStatus.PreconditionFailed => "The item was changed by someone else.",
        _ => Status.ToString(),
    };
}

/// <summary>
/// Reads and writes list items for the API and for workflows: values are checked against the list's fields, and every
/// change is saved with its event (<see cref="ItemCreated"/>, <see cref="ItemUpdated"/>, <see cref="ItemDeleted"/>)
/// through the outbox. Queries copy their arguments into locals (precompiled queries, ADR-0039).
/// </summary>
public sealed class ListItemService(CoreDb db, IOutbox outbox)
{
    public Task<ListDefinition?> FindListAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var id = listId;
        var ct = cancellationToken;
        return context.Lists.Where(l => l.TenantId == tenant && l.Id == id).FirstOrDefaultAsync(ct);
    }

    public Task<ListDefinition?> FindListByNameAsync(Guid tenantId, string listName, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var name = listName;
        var ct = cancellationToken;
        return context.Lists.Where(l => l.TenantId == tenant && l.Name == name).FirstOrDefaultAsync(ct);
    }

    public Task<ListItem?> FindAsync(Guid tenantId, Guid listId, Guid itemId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var id = itemId;
        var ct = cancellationToken;
        return context.Items.Where(i => i.TenantId == tenant && i.ListId == list && i.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Creates an item; with <paramref name="itemId"/> (e.g. a workflow's execution id) a repeat finds the item it created.</summary>
    public async Task<ItemWriteResult> CreateAsync(ItemActor actor, Guid listId, Guid? itemId, JsonObject values, CancellationToken cancellationToken)
    {
        if (await FindListAsync(actor.TenantId, listId, cancellationToken) is not { } list)
        {
            return new(ItemWriteStatus.NotFound);
        }

        var fields = ListEndpoints.FieldsOf(list);
        if (itemId is { } requested && await FindAsync(actor.TenantId, listId, requested, cancellationToken) is { } existing)
        {
            return new(ItemWriteStatus.Exists, existing, fields);
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
        return new(ItemWriteStatus.Ok, item, fields);
    }

    /// <summary>Changes values (<c>null</c> removes one); <paramref name="ifMatch"/> is the version the caller saw, if any.</summary>
    public async Task<ItemWriteResult> UpdateAsync(ItemActor actor, Guid listId, Guid itemId, JsonObject values, uint? ifMatch, CancellationToken cancellationToken)
    {
        if (await FindListAsync(actor.TenantId, listId, cancellationToken) is not { } list
            || await FindAsync(actor.TenantId, listId, itemId, cancellationToken) is not { } item)
        {
            return new(ItemWriteStatus.NotFound);
        }

        if (ifMatch is { } version && item.Version != version)
        {
            return new(ItemWriteStatus.PreconditionFailed);
        }

        var fields = ListEndpoints.FieldsOf(list);
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

        return new(ItemWriteStatus.Ok, item, fields);
    }

    public async Task<ItemWriteResult> DeleteAsync(ItemActor actor, Guid listId, Guid itemId, uint? ifMatch, CancellationToken cancellationToken)
    {
        if (await FindAsync(actor.TenantId, listId, itemId, cancellationToken) is not { } item)
        {
            return new(ItemWriteStatus.NotFound);
        }

        if (ifMatch is { } version && item.Version != version)
        {
            return new(ItemWriteStatus.PreconditionFailed);
        }

        db.Items.Remove(item);
        await outbox.SaveChangesAsync(db, [new ItemDeleted(listId, item.Id, item.Title) { TenantId = actor.TenantId, UserId = actor.UserId, Depth = actor.Depth }], cancellationToken);
        return new(ItemWriteStatus.Ok, item);
    }
}
