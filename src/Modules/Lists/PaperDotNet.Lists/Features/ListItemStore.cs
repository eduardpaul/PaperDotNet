using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// <see cref="IListItemStore"/> over the same loader, query runner and writer as the items API, so code gets the API's
/// validation, mutators and events. Bound to a caller: the one of the current request, or one given with
/// <see cref="ActingAs"/>/<see cref="AsSystem(ChangeActor)"/>.
/// </summary>
internal sealed class ListItemStore(
    ListsDbContext db, ListSchemaLoader loader, ItemQueryRunner runner, ItemWriter writer, IWorkspaceAccess workspaces, ICurrentUser user,
    ItemSearchDocuments search, HomeLibraries home, ListCaller? bound = null)
    : IListItemStore
{
    private ListCaller Caller => bound ?? new ListCaller(
        user.TenantId ?? throw new InvalidOperationException("The item store needs a caller: use ActingAs or AsSystem outside a request."), user.UserId);

    public IListItemStore ActingAs(ChangeActor actor) => new ListItemStore(db, loader, runner, writer, workspaces, user, search, home, ListCaller.From(actor));

    public IListItemStore AsSystem(ChangeActor actor) => new ListItemStore(db, loader, runner, writer, workspaces, user, search, home, ListCaller.From(actor, system: true));

    public Task<HomeData> EnsureHomeAsync(CancellationToken cancellationToken) =>
        home.EnsureAsync(Caller.TenantId, Caller.UserId ?? throw new InvalidOperationException("Home belongs to a user."), cancellationToken);

    public IListItemStore AsSystem() => AsSystem(Caller.Actor);

    public async Task<IReadOnlyList<ListData>> GetListsAsync(Guid? workspaceId, string? templateKey, CancellationToken cancellationToken)
    {
        var caller = Caller;
        List<ListDefinition> lists;
        if (caller.System)
        {
            lists = await AllListsAsync(caller.TenantId, workspaceId, cancellationToken);
        }
        else if (caller.UserId is not { } userId)
        {
            lists = [];
        }
        else
        {
            var memberships = workspaceId is { } id
                ? [new WorkspaceMembership(id, await workspaces.GetPermissionAsync(caller.TenantId, userId, id, cancellationToken))]
                : await workspaces.GetMembershipsAsync(caller.TenantId, userId, cancellationToken);
            lists = await loader.VisibleListsAsync(caller, memberships, cancellationToken);
        }

        var result = new List<ListData>();
        foreach (var list in lists.Where(l => templateKey is null || l.TemplateKey == templateKey))
        {
            var contentTypes = await loader.ContentTypesAsync(caller.TenantId, ListsJsonText.Ids(list.ContentTypeIds), cancellationToken);
            result.Add(ToData(list, contentTypes, null));
        }

        return result;
    }

    private Task<List<ListDefinition>> AllListsAsync(Guid tenantId, Guid? workspaceId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var ct = cancellationToken;
        if (workspaceId is { } id)
        {
            var workspace = id;
            return context.Lists.AsNoTracking().Where(l => l.TenantId == tenant && l.WorkspaceId == workspace && l.DeletedAt == null).OrderBy(l => l.Name).ToListAsync(ct);
        }

        return context.Lists.AsNoTracking().Where(l => l.TenantId == tenant && l.DeletedAt == null).OrderBy(l => l.Name).ToListAsync(ct);
    }

    public async Task<ListData?> GetListAsync(Guid workspaceId, Guid listId, CancellationToken cancellationToken) =>
        await loader.LoadAsync(Caller, workspaceId, listId, cancellationToken) is { } schema
            ? ToData(schema.List, schema.ContentTypes, schema.Access.ListLevel)
            : null;

    public async Task<ListDescription?> DescribeListAsync(Guid workspaceId, Guid listId, CancellationToken cancellationToken)
    {
        var schema = await loader.LoadAsync(Caller, workspaceId, listId, cancellationToken);
        return schema is null
            ? null
            : new ListDescription(
                schema.List.Id, schema.List.WorkspaceId, schema.List.Name, schema.List.Description, schema.List.TemplateKey,
                schema.List.Kind == ListKinds.Library, schema.List.AllowFolders, schema.Access.ListLevel,
                [.. schema.ContentTypes.Select(c => new ListContentTypeInfo(c.Id, c.Name, c.Key, c.Entity.Description, [.. c.Fields.Select(FieldInfo)]))]);
    }

    public async Task<ListItemData?> GetAsync(Guid workspaceId, Guid listId, Guid itemId, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        var item = schema is null ? null : await writer.FindAsync(caller.TenantId, listId, itemId, cancellationToken);
        return item is null || schema!.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Read ? null : ToData(schema, item);
    }

    public async Task<(IReadOnlyList<ListItemData> Items, string? Error)> QueryAsync(Guid workspaceId, Guid listId, ListItemQuery query, CancellationToken cancellationToken)
    {
        var (page, error) = await QueryPageAsync(workspaceId, listId, query, cancellationToken);
        return (page?.Items ?? [], error);
    }

    public Task<(ListItemPage? Page, string? Error)> QueryPageAsync(Guid workspaceId, Guid listId, ListItemQuery query, CancellationToken cancellationToken) =>
        PageAsync(workspaceId, listId, query, FolderMode.ItemsOnly, null, cancellationToken);

    public async Task<(ListItemPage? Page, string? Error)> ListChildrenAsync(
        Guid workspaceId, Guid listId, Guid? folderId, ListItemQuery query, CancellationToken cancellationToken)
    {
        if (folderId is { } id && await GetAsync(workspaceId, listId, id, cancellationToken) is not { IsFolder: true })
        {
            return (null, "The folder was not found (or you cannot read it).");
        }

        return await PageAsync(workspaceId, listId, query, FolderMode.Children, folderId, cancellationToken);
    }

    private async Task<(ListItemPage? Page, string? Error)> PageAsync(
        Guid workspaceId, Guid listId, ListItemQuery query, FolderMode folders, Guid? parentId, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return (null, "The list was not found.");
        }

        var (result, error) = await runner.RunAsync(caller, schema, [query.Filter], query.OrderBy, query.Top, query.SkipToken, false, folders, parentId, cancellationToken);
        return result is null ? (null, error) : (new ListItemPage([.. result.Items.Select(i => ToData(schema, i))], result.NextCursor), null);
    }

    public async Task<(IReadOnlyList<ListQueryResult> Results, string? Error)> QueryAsync(
        IReadOnlyList<ListData> lists, ListItemQuery query, CancellationToken cancellationToken)
    {
        var results = new List<ListQueryResult>();
        foreach (var list in lists)
        {
            var caller = Caller;
            if (await loader.LoadAsync(caller, list.WorkspaceId, list.Id, cancellationToken) is not { } schema)
            {
                continue;
            }

            var (result, error) = await runner.RunAsync(caller, schema, [query.Filter], query.OrderBy, query.Top, null, false, FolderMode.ItemsOnly, null, cancellationToken);
            if (result is null)
            {
                return ([], error);
            }

            results.Add(new ListQueryResult(list, [.. result.Items.Select(i => ToData(schema, i))]));
        }

        return (results, null);
    }

    public Task<ListItemResult> CreateAsync(Guid workspaceId, Guid listId, JsonObject fields, Guid? contentTypeId, CancellationToken cancellationToken) =>
        CreateAsync(workspaceId, listId, fields, contentTypeId, null, cancellationToken);

    public async Task<ListItemResult> CreateAsync(Guid workspaceId, Guid listId, JsonObject fields, Guid? contentTypeId, Guid? parentId, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        return schema is null
            ? new ListItemResult(ListItemStatus.NotFound)
            : ToResult(schema, await writer.CreateAsync(caller, schema, contentTypeId, parentId, isFolder: false, Element(fields), Ids.New(), cancellationToken));
    }

    public async Task<ListItemResult> CreateFolderAsync(Guid workspaceId, Guid listId, string title, Guid? parentId, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        return schema is null
            ? new ListItemResult(ListItemStatus.NotFound)
            : ToResult(schema, await writer.CreateAsync(caller, schema, null, parentId, isFolder: true, Element(new JsonObject { ["title"] = title }), Ids.New(), cancellationToken));
    }

    public Task<ListItemResult> CreateAsync(Guid workspaceId, Guid listId, Guid itemId, JsonObject fields, Guid? contentTypeId, CancellationToken cancellationToken) =>
        CreateAsync(workspaceId, listId, itemId, fields, contentTypeId, null, cancellationToken);

    public async Task<ListItemResult> CreateAsync(
        Guid workspaceId, Guid listId, Guid itemId, JsonObject fields, Guid? contentTypeId, Guid? parentId, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        if (await FindAnyAsync(caller.TenantId, itemId, cancellationToken) is { } existing)
        {
            return existing.ListId == listId && existing.DeletedAt is null
                ? new ListItemResult(ListItemStatus.Ok, ToData(schema, existing))
                : new ListItemResult(ListItemStatus.Rejected, Message: "An item with this id exists in another list or was deleted.");
        }

        return ToResult(schema, await writer.CreateAsync(caller, schema, contentTypeId, parentId, isFolder: false, Element(fields), itemId, cancellationToken));
    }

    /// <summary>An item of the tenant with this id, deleted or not (ids are unique across lists).</summary>
    internal Task<ListItem?> FindAnyAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        return context.Items.AsNoTracking().Where(i => i.TenantId == tenant && i.Id == id).FirstOrDefaultAsync(ct);
    }

    public async Task<ListItemResult> UpdateAsync(Guid workspaceId, Guid listId, Guid itemId, JsonObject fields, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var (schema, item, problem) = await LoadForChangeAsync(caller, workspaceId, listId, itemId, expectedVersion, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        try
        {
            return ToResult(schema!, await writer.UpdateAsync(caller, schema!, item!, null, Optional<Guid?>.None, Element(fields), cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ListItemResult(ListItemStatus.VersionMismatch);
        }
    }

    public async Task<(Guid? FolderId, ListItemResult? Problem)> EnsureFolderAsync(
        Guid workspaceId, Guid listId, IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var schema = await loader.LoadAsync(caller, workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return (null, new ListItemResult(ListItemStatus.NotFound));
        }

        Guid? parentId = null;
        foreach (var segment in path.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var existing = await FindFolderAsync(caller.TenantId, listId, parentId, segment, cancellationToken);
            if (existing is not null)
            {
                if (schema.Access.Level(existing.ScopeId) < WorkspaceAccessLevel.Read)
                {
                    return (null, new ListItemResult(ListItemStatus.Forbidden));
                }

                parentId = existing.Id;
                continue;
            }

            var created = await writer.CreateAsync(caller, schema, null, parentId, isFolder: true, Element(new JsonObject { ["title"] = segment }), Ids.New(), cancellationToken);
            if (created.Item is null)
            {
                return (null, ToResult(schema, created));
            }

            parentId = created.Item.Id;
        }

        return (parentId, null);
    }

    private Task<ListItem?> FindFolderAsync(Guid tenantId, Guid listId, Guid? parentId, string title, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var list = listId;
        var name = title;
        var ct = cancellationToken;
        if (parentId is { } id)
        {
            var parent = id;
            return context.Items.AsNoTracking()
                .Where(i => i.TenantId == tenant && i.ListId == list && i.IsFolder && i.ParentId == parent && i.Title == name && i.DeletedAt == null)
                .OrderBy(i => i.Id).FirstOrDefaultAsync(ct);
        }

        return context.Items.AsNoTracking()
            .Where(i => i.TenantId == tenant && i.ListId == list && i.IsFolder && i.ParentId == null && i.Title == name && i.DeletedAt == null)
            .OrderBy(i => i.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<ListItemResult> MoveAsync(Guid workspaceId, Guid listId, Guid itemId, Guid? folderId, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var (schema, item, problem) = await LoadForChangeAsync(caller, workspaceId, listId, itemId, null, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (item!.ParentId == folderId)
        {
            return new ListItemResult(ListItemStatus.Ok, ToData(schema!, item));
        }

        try
        {
            return ToResult(schema!, await writer.UpdateAsync(caller, schema!, item, null, Optional<Guid?>.Of(folderId), null, cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ListItemResult(ListItemStatus.VersionMismatch);
        }
    }

    public async Task<ListItemResult> DeleteAsync(Guid workspaceId, Guid listId, Guid itemId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var caller = Caller;
        var (schema, item, problem) = await LoadForChangeAsync(caller, workspaceId, listId, itemId, expectedVersion, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        try
        {
            return ToResult(schema!, await writer.DeleteAsync(caller, schema!, item!, cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ListItemResult(ListItemStatus.VersionMismatch);
        }
    }

    /// <summary>The list and a tracked item the caller may change, checking the expected version when given.</summary>
    private async Task<(ListSchema? Schema, ListItem? Item, ListItemResult? Problem)> LoadForChangeAsync(
        ListCaller caller, Guid workspaceId, Guid listId, Guid itemId, uint? expectedVersion, CancellationToken ct)
    {
        var schema = await loader.LoadAsync(caller, workspaceId, listId, ct);
        var item = schema is null ? null : await ItemEndpoints.FindTrackedAsync(db, caller.TenantId, listId, itemId, ct);
        var level = item is null ? WorkspaceAccessLevel.None : schema!.Access.Level(item.ScopeId);
        if (level == WorkspaceAccessLevel.None)
        {
            return (null, null, new ListItemResult(ListItemStatus.NotFound));
        }

        if (level < WorkspaceAccessLevel.Contribute)
        {
            return (null, null, new ListItemResult(ListItemStatus.Forbidden));
        }

        if (expectedVersion is { } version && version != item!.Version)
        {
            return (null, null, new ListItemResult(ListItemStatus.VersionMismatch));
        }

        return (schema, item, null);
    }

    private static ListData ToData(ListDefinition list, IReadOnlyList<ContentTypeSchema> contentTypes, WorkspaceAccessLevel? access) =>
        new(list.Id, list.WorkspaceId, list.Name, list.TemplateKey)
        {
            IsLibrary = list.Kind == ListKinds.Library,
            Access = access,
            ContentTypeKeys = [.. contentTypes.Select(c => c.Key).OfType<string>()],
            ContentTypes = [.. contentTypes.Select(c => new ListContentType(c.Id, c.Name, c.Key))],
        };

    private static ListFieldInfo FieldInfo(FieldDefinition field) => new(
        field.Name, field.DisplayName, field.Type, field.Required, field.AllowMultiple, field.Description,
        field.MaxLength, field.Minimum, field.Maximum, field.Choices.Count > 0 ? field.Choices : null);

    private static JsonElement Element(JsonObject fields) => JsonSerializer.SerializeToElement(fields, ListsJson.Default.JsonObject);

    private static ListItemResult ToResult(ListSchema schema, ItemWriteResult result) => result switch
    {
        { Forbidden: true } => new ListItemResult(ListItemStatus.Forbidden),
        { Errors: { } errors } => new ListItemResult(ListItemStatus.Invalid, Errors: errors),
        { Cancelled: { } message } => new ListItemResult(ListItemStatus.Rejected, Message: message),
        { Conflict: { } message } => new ListItemResult(ListItemStatus.Rejected, Message: message),
        _ => new ListItemResult(ListItemStatus.Ok, ToData(schema, result.Item!)),
    };

    internal static ListItemData ToData(ListSchema schema, ListItem item) => new(
        item.Id, schema.List.WorkspaceId, item.ListId, item.ContentTypeId, item.ParentId, item.IsFolder, item.Version,
        item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, ItemWriter.Values(item))
    {
        Access = schema.Access.Level(item.ScopeId),
    };

    public Task ReindexAsync(Guid itemId, CancellationToken cancellationToken) => search.IndexItemAsync(Caller.TenantId, itemId, cancellationToken);
}
