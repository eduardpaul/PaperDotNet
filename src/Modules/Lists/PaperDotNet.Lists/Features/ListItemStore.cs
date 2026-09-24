using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// <see cref="IListItemStore"/> over the same loader, query runner and writer as the items API,
/// so code gets the API's validation, receivers, versions and events.
/// </summary>
internal sealed class ListItemStore(
    ListsDbContext db, ListSchemaLoader loader, ItemQueryRunner runner, ItemWriter writer, IWorkspaceAccess workspaces,
    ListItemSearchDocuments search, bool system = false)
    : IListItemStore
{
    public IListItemStore AsSystem() => system ? this : new ListItemStore(db, loader, runner, writer, workspaces, search, system: true);

    public Task ReindexAsync(Guid itemId, CancellationToken cancellationToken) => search.IndexItemAsync(itemId, cancellationToken);

    public async Task<IReadOnlyList<ListData>> GetListsAsync(Guid? workspaceId, string? templateKey, CancellationToken cancellationToken)
    {
        List<ListDefinition> lists;
        if (system)
        {
            var query = db.Lists.AsNoTracking();
            if (workspaceId is { } id)
            {
                query = query.Where(l => l.WorkspaceId == id);
            }

            lists = await query.OrderBy(l => l.Name).ToListAsync(cancellationToken);
        }
        else
        {
            var memberships = workspaceId is { } id
                ? [new WorkspaceMembership(id, await workspaces.GetPermissionAsync(id, cancellationToken))]
                : await workspaces.GetMyWorkspacesAsync(cancellationToken);
            lists = [];
            foreach (var membership in memberships.Where(m => m.Level > WorkspaceAccessLevel.None))
            {
                lists.AddRange(await loader.VisibleListsAsync(membership.WorkspaceId, membership.Level, cancellationToken));
            }
        }

        lists = lists.Where(l => templateKey is null || l.TemplateKey == templateKey).ToList();
        var contentTypeIds = lists.SelectMany(l => l.ContentTypeIds).Distinct().ToList();
        var keys = await db.ContentTypes.AsNoTracking()
            .Where(c => contentTypeIds.Contains(c.Id) && c.Key != null)
            .ToDictionaryAsync(c => c.Id, c => c.Key!, cancellationToken);
        return lists
            .Select(l => new ListData(l.Id, l.WorkspaceId, l.Name, l.TemplateKey)
            {
                IsLibrary = l.Kind == ListKind.Library,
                ContentTypeKeys = [.. l.ContentTypeIds.Where(keys.ContainsKey).Select(id => keys[id])],
            })
            .ToList();
    }

    public async Task<ListData?> GetListAsync(Guid workspaceId, Guid listId, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        return schema is null
            ? null
            : new ListData(schema.List.Id, schema.List.WorkspaceId, schema.List.Name, schema.List.TemplateKey)
            {
                IsLibrary = schema.List.Kind == ListKind.Library,
                Access = schema.Access.ListLevel,
                ContentTypeKeys = [.. schema.ContentTypes.Where(c => c.Key is not null).Select(c => c.Key!)],
            };
    }

    public async Task<HomeData> EnsureHomeAsync(CancellationToken cancellationToken)
    {
        var home = await HomeEndpoints.EnsureHomeAsync(workspaces, db, cancellationToken);
        return new HomeData(home.WorkspaceId, home.DocumentsListId, home.InboxListId);
    }

    public async Task<ListItemData?> GetAsync(Guid workspaceId, Guid listId, Guid itemId, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        var item = schema is null ? null : await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, cancellationToken);
        return item is null || schema!.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Read ? null : ToData(schema, item);
    }

    public async Task<(IReadOnlyList<ListItemData> Items, string? Error)> QueryAsync(
        Guid workspaceId, Guid listId, ListItemQuery query, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return ([], "The list was not found.");
        }

        var (items, error) = await runner.ListAsync(schema, query.Filter, query.OrderBy, query.Top, cancellationToken);
        return items is null ? ([], error) : (items.Select(i => ToData(schema, i)).ToList(), null);
    }

    public async Task<ListItemResult> CreateAsync(Guid workspaceId, Guid listId, JsonObject fields, Guid? contentTypeId, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        return ToResult(schema, await writer.CreateAsync(schema, contentTypeId, null, isFolder: false, Element(fields), cancellationToken));
    }

    public async Task<ListItemResult> UpdateAsync(
        Guid workspaceId, Guid listId, Guid itemId, JsonObject fields, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var (schema, item, problem) = await LoadForChangeAsync(workspaceId, listId, itemId, expectedVersion, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        try
        {
            return ToResult(schema!, await writer.UpdateAsync(schema!, item!, null, Optional<Guid?>.None, Element(fields), cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ListItemResult(ListItemStatus.VersionMismatch);
        }
    }

    public async Task<ListItemResult> DeleteAsync(Guid workspaceId, Guid listId, Guid itemId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var (schema, item, problem) = await LoadForChangeAsync(workspaceId, listId, itemId, expectedVersion, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        try
        {
            return ToResult(schema!, await writer.DeleteAsync(schema!, item!, cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ListItemResult(ListItemStatus.VersionMismatch);
        }
    }

    private Task<ListSchema?> LoadAsync(Guid workspaceId, Guid listId, CancellationToken ct) =>
        system ? loader.LoadAsSystemAsync(workspaceId, listId, ct) : loader.LoadAsync(workspaceId, listId, ct);

    private async Task<(ListSchema? Schema, ListItem? Item, ListItemResult? Problem)> LoadForChangeAsync(
        Guid workspaceId, Guid listId, Guid itemId, uint? expectedVersion, CancellationToken ct)
    {
        var schema = await LoadAsync(workspaceId, listId, ct);
        var item = schema is null ? null : await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);
        var level = item is null ? WorkspaceAccessLevel.None : schema!.Access.Level(item.ScopeId);
        if (level == WorkspaceAccessLevel.None)
        {
            return (null, null, new ListItemResult(ListItemStatus.NotFound));
        }

        if (level < WorkspaceAccessLevel.Contribute)
        {
            return (null, null, new ListItemResult(ListItemStatus.Forbidden));
        }

        if (expectedVersion is { } version)
        {
            if (version != item!.Version)
            {
                return (null, null, new ListItemResult(ListItemStatus.VersionMismatch));
            }

            db.Entry(item).Property(i => i.Version).OriginalValue = version;
        }

        return (schema, item, null);
    }

    private static JsonElement Element(JsonObject fields) => JsonSerializer.SerializeToElement(fields);

    private static ListItemResult ToResult(ListSchema schema, ItemWriteResult result) => result switch
    {
        { Forbidden: true } => new ListItemResult(ListItemStatus.Forbidden),
        { Errors: { } errors } => new ListItemResult(ListItemStatus.Invalid, Errors: errors),
        { Cancelled: { } message } => new ListItemResult(ListItemStatus.Rejected, Message: message),
        { Conflict: { } message } => new ListItemResult(ListItemStatus.Rejected, Message: message),
        _ => new ListItemResult(ListItemStatus.Ok, ToData(schema, result.Item!)),
    };

    private static ListItemData ToData(ListSchema schema, ListItem item) => new(
        item.Id, schema.List.WorkspaceId, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.Version,
        item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, ItemWriter.Values(item))
    {
        Access = schema.Access.Level(item.ScopeId),
    };
}
