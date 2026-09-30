using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Workflows.Features;

/// <summary>
/// The lists of a workflow's workspace, for runs: workflows act on behalf of the organization (full control in the
/// workspace, ADR-0036), with the causation depth of the run's changes.
/// </summary>
public sealed class WorkflowItems(IListItemStore store)
{
    public Task<ListData?> FindListAsync(ChangeActor actor, Guid workspaceId, Guid listId, CancellationToken cancellationToken) =>
        store.AsSystem(actor).GetListAsync(workspaceId, listId, cancellationToken);

    public async Task<ListData?> FindListByNameAsync(ChangeActor actor, Guid workspaceId, string name, CancellationToken cancellationToken) =>
        (await store.AsSystem(actor).GetListsAsync(workspaceId, null, cancellationToken)).FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));

    public Task<ListItemData?> GetAsync(ChangeActor actor, Guid workspaceId, Guid listId, Guid itemId, CancellationToken cancellationToken) =>
        store.AsSystem(actor).GetAsync(workspaceId, listId, itemId, cancellationToken);

    /// <summary>Items matching an OData filter and order (optionally only <paramref name="itemId"/>), or the reason the query is invalid.</summary>
    public Task<(IReadOnlyList<ListItemData> Items, string? Error)> QueryAsync(
        ChangeActor actor, Guid workspaceId, Guid listId, string? filter, string? orderBy, int top, Guid? itemId, CancellationToken cancellationToken)
    {
        var condition = itemId is { } id ? $"id eq {id}" + (string.IsNullOrWhiteSpace(filter) ? "" : $" and ({filter})") : filter;
        return store.AsSystem(actor).QueryAsync(workspaceId, listId, new ListItemQuery(condition, orderBy, top), cancellationToken);
    }

    public Task<ListItemResult> CreateAsync(ChangeActor actor, Guid workspaceId, Guid listId, Guid? itemId, JsonObject fields, CancellationToken cancellationToken) =>
        itemId is { } id
            ? store.AsSystem(actor).CreateAsync(workspaceId, listId, id, fields, null, cancellationToken)
            : store.AsSystem(actor).CreateAsync(workspaceId, listId, fields, null, cancellationToken);

    public Task<ListItemResult> UpdateAsync(ChangeActor actor, Guid workspaceId, Guid listId, Guid itemId, JsonObject fields, CancellationToken cancellationToken) =>
        store.AsSystem(actor).UpdateAsync(workspaceId, listId, itemId, fields, null, cancellationToken);

    public Task<ListItemResult> DeleteAsync(ChangeActor actor, Guid workspaceId, Guid listId, Guid itemId, CancellationToken cancellationToken) =>
        store.AsSystem(actor).DeleteAsync(workspaceId, listId, itemId, null, cancellationToken);
}
