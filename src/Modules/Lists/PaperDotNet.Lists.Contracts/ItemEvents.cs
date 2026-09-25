using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Lists.Contracts;

public enum ItemEventKind
{
    Adding,
    Updating,
    Deleting,
}

/// <summary>Where an item write happens; mutators use it to decide whether they apply.</summary>
public sealed record ItemEventScope(Guid WorkspaceId, Guid ListId, string ListName, Guid ContentTypeId, bool IsFolder)
{
    /// <summary>Name of the item's content type (e.g. <c>Invoice</c>).</summary>
    public string? ContentTypeName { get; init; }

    /// <summary>Template key of the item's content type (e.g. <c>event</c>); null for custom content types.</summary>
    public string? ContentTypeKey { get; init; }

    /// <summary>Key of the template the list was created from (e.g. <c>tasks</c>), if any.</summary>
    public string? ListTemplate { get; init; }
}

/// <summary>
/// An item write about to be saved. Mutators may change <see cref="After"/> (it is validated again)
/// or <see cref="Cancel"/> the write.
/// </summary>
public sealed class ItemMutationContext
{
    public required ItemEventKind Kind { get; init; }

    public required ItemEventScope Scope { get; init; }

    public required Guid ItemId { get; init; }

    public Guid? UserId { get; init; }

    /// <summary>Stored values (including <c>title</c>) before the change; null when adding.</summary>
    public JsonObject? Before { get; init; }

    /// <summary>Values that will be saved (including <c>title</c>); editable. Null when deleting.</summary>
    public JsonObject? After { get; init; }

    public bool IsCancelled => CancelMessage is not null;

    public string? CancelMessage { get; private set; }

    /// <summary>Cancels the write; the caller receives the message (HTTP 409 <c>cancelledByMutator</c>).</summary>
    public void Cancel(string message) => CancelMessage = message;
}

/// <summary>
/// An item mutator (ADR-0023): runs synchronously inside an item write, before it is saved, and can
/// change the values or cancel the write. Mutators must be stateless and fast; they run on the server
/// handling the request. Everything that reacts to a saved change is an <see cref="IEventSubscriber{TEvent}"/>
/// of <see cref="ItemAdded"/>, <see cref="ItemUpdated"/>, <see cref="ItemDeleted"/> (or an automation).
/// </summary>
public interface IItemMutator
{
    /// <summary>Lower runs first.</summary>
    int Sequence => 1000;

    bool AppliesTo(ItemEventScope scope) => true;

    /// <summary>Asynchronous variant (e.g. to check per-tenant settings); defaults to <see cref="AppliesTo"/>.</summary>
    ValueTask<bool> AppliesToAsync(ItemEventScope scope, CancellationToken cancellationToken) => ValueTask.FromResult(AppliesTo(scope));

    ValueTask ItemAddingAsync(ItemMutationContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask ItemUpdatingAsync(ItemMutationContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask ItemDeletingAsync(ItemMutationContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Base for item integration events: published with the change (transactional outbox) and handled in the background.</summary>
public abstract record ItemEvent : IntegrationEvent
{
    public required Guid WorkspaceId { get; init; }

    public required Guid ListId { get; init; }

    public required Guid ItemId { get; init; }

    public required Guid ContentTypeId { get; init; }

    public bool IsFolder { get; init; }

    public IReadOnlyList<string> ChangedFields { get; init; } = [];
}

public sealed record ItemAdded : ItemEvent;

public sealed record ItemUpdated : ItemEvent;

public sealed record ItemDeleted : ItemEvent;

/// <summary>An item was restored from the recycle bin.</summary>
public sealed record ItemRestored : ItemEvent;

/// <summary>An item was deleted permanently (purged from the recycle bin); remove data kept for it.</summary>
public sealed record ItemPurged : ItemEvent;
