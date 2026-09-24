using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Lists.Contracts;

public enum ItemEventKind
{
    Adding,
    Updating,
    Deleting,
}

/// <summary>Where an item event happens; receivers use it to decide whether they apply.</summary>
public sealed record ItemEventScope(Guid WorkspaceId, Guid ListId, string ListName, Guid ContentTypeId, bool IsFolder);

/// <summary>
/// Context of a synchronous <b>before</b> event (SharePoint "…ing"): runs before
/// the change is saved. Receivers may change <see cref="After"/> or <see cref="Cancel"/>.
/// </summary>
public sealed class ItemChangingContext
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

    /// <summary>Cancels the operation; the caller receives the message (HTTP 409 <c>cancelledByReceiver</c>).</summary>
    public void Cancel(string message) => CancelMessage = message;
}

/// <summary>Context of a synchronous <b>after</b> event (SharePoint "…ed"): the change is committed.</summary>
public sealed record ItemChangedContext(
    ItemEventKind Kind, ItemEventScope Scope, Guid ItemId, Guid? UserId, JsonObject? Before, JsonObject? After, IReadOnlyCollection<string> ChangedFields);

/// <summary>
/// An item event receiver (SharePoint <c>SPItemEventReceiver</c> style). Before
/// methods run synchronously inside the write and can modify or cancel it;
/// after methods run synchronously after commit (errors are logged, never undo
/// the change). For background processing use <see cref="IEventSubscriber{TEvent}"/>
/// with <see cref="ItemAdded"/>, <see cref="ItemUpdated"/> or <see cref="ItemDeleted"/>.
/// </summary>
public interface IItemEventReceiver
{
    /// <summary>Lower runs first.</summary>
    int Sequence => 1000;

    bool AppliesTo(ItemEventScope scope) => true;

    ValueTask ItemAddingAsync(ItemChangingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask ItemUpdatingAsync(ItemChangingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask ItemDeletingAsync(ItemChangingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask ItemAddedAsync(ItemChangedContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask ItemUpdatedAsync(ItemChangedContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask ItemDeletedAsync(ItemChangedContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Base for item integration events (asynchronous after events).</summary>
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
