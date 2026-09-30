using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Lists.Contracts;

/// <summary>A list, by id and name.</summary>
public sealed record ListData(Guid Id, string Name);

/// <summary>An item as the API shows it: <see cref="Fields"/> holds the title and the values of the list's fields.</summary>
public sealed record ListItemData(
    Guid Id, Guid ListId, JsonObject Fields, DateTimeOffset CreatedAt, Guid? CreatedBy, DateTimeOffset UpdatedAt, Guid? UpdatedBy, uint Version);

public enum ItemWriteStatus
{
    Ok,
    NotFound,
    Invalid,
    PreconditionFailed,

    /// <summary>An item with the requested id exists already (a repeated create).</summary>
    Exists,
}

/// <summary>The outcome of a write: the item when it succeeded (or existed), else the reason.</summary>
public sealed record ItemWriteResult(ItemWriteStatus Status, ListItemData? Item = null, IReadOnlyDictionary<string, string[]>? Errors = null)
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
/// Items of the tenant's lists, for other modules (workflows) and the Lists API alike: values are checked against the
/// list's fields, and every change is saved with its event (<see cref="ItemCreated"/>, <see cref="ItemUpdated"/>,
/// <see cref="ItemDeleted"/>) through the outbox, with the actor's causation depth.
/// </summary>
public interface IListItemStore
{
    Task<ListData?> FindListAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken);

    Task<ListData?> FindListByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    Task<ListItemData?> GetAsync(Guid tenantId, Guid listId, Guid itemId, CancellationToken cancellationToken);

    /// <summary>Items matching an OData <c>$filter</c> and <c>$orderby</c> (optionally only <paramref name="itemId"/>); or the reason the query is invalid.</summary>
    Task<(IReadOnlyList<ListItemData> Items, string? Error)> QueryAsync(
        Guid tenantId, Guid listId, string? filter, string? orderBy, int top, Guid? itemId, CancellationToken cancellationToken);

    /// <summary>Creates an item; with <paramref name="itemId"/> (e.g. a workflow's execution id) a repeat finds the item it created.</summary>
    Task<ItemWriteResult> CreateAsync(ChangeActor actor, Guid listId, Guid? itemId, JsonObject values, CancellationToken cancellationToken);

    /// <summary>Changes values (<c>null</c> removes one); <paramref name="ifMatch"/> is the version the caller saw, if any.</summary>
    Task<ItemWriteResult> UpdateAsync(ChangeActor actor, Guid listId, Guid itemId, JsonObject values, uint? ifMatch, CancellationToken cancellationToken);

    Task<ItemWriteResult> DeleteAsync(ChangeActor actor, Guid listId, Guid itemId, uint? ifMatch, CancellationToken cancellationToken);
}
