using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// <see cref="IListItemStore"/> over the same loader, query runner and writer as the items API,
/// so code gets the API's validation, mutators, versions and events.
/// </summary>
internal sealed partial class ListItemStore(
    ListsDbContext db, ListSchemaLoader loader, ItemQueryRunner runner, ItemWriter writer, IWorkspaceAccess workspaces,
    ListItemSearchDocuments search, ContentTypeProvisioner contentTypes, ListTemplateRegistry templates, RelationshipTypes relationshipTypes, bool system = false)
    : IListItemStore
{
    public IListItemStore AsSystem() => system ? this : new ListItemStore(db, loader, runner, writer, workspaces, search, contentTypes, templates, relationshipTypes, system: true);

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
            lists = await loader.VisibleListsAsync(memberships, cancellationToken);
        }

        lists = lists.Where(l => templateKey is null || l.TemplateKey == templateKey).ToList();
        var contentTypeIds = lists.SelectMany(l => l.ContentTypeIds).Distinct().ToList();
        var contentTypes = await db.ContentTypes.AsNoTracking()
            .Where(c => contentTypeIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new ListContentType(c.Id, c.Name, c.Key), cancellationToken);
        return lists
            .Select(l => new ListData(l.Id, l.WorkspaceId, l.Name, l.TemplateKey)
            {
                IsLibrary = l.Kind == ListKind.Library,
                ContentTypeKeys = [.. l.ContentTypeIds.Select(contentTypes.GetValueOrDefault).OfType<ListContentType>().Select(c => c.Key).OfType<string>()],
                ContentTypes = [.. l.ContentTypeIds.Select(contentTypes.GetValueOrDefault).OfType<ListContentType>()],
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
                ContentTypes = [.. schema.ContentTypes.Select(c => new ListContentType(c.Id, c.Name, c.Key))],
            };
    }

    public async Task<ListDescription?> DescribeListAsync(Guid workspaceId, Guid listId, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        return schema is null
            ? null
            : new ListDescription(
                schema.List.Id, schema.List.WorkspaceId, schema.List.Name, schema.List.Description, schema.List.TemplateKey,
                schema.List.Kind == ListKind.Library, schema.List.AllowFolders, schema.Access.ListLevel,
                schema.ContentTypes.Select(c => new ListContentTypeInfo(c.Id, c.Name, c.Key, c.Description, c.Fields.Select(FieldInfo).ToList())).ToList());
    }

    public async Task<HomeData> EnsureHomeAsync(CancellationToken cancellationToken)
    {
        var home = await HomeEndpoints.EnsureHomeAsync(workspaces, db, contentTypes, templates, cancellationToken);
        return new HomeData(home.WorkspaceId, home.DocumentsListId, home.InboxListId);
    }

    public async Task<ListItemData?> GetAsync(Guid workspaceId, Guid listId, Guid itemId, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        var item = schema is null ? null : await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, cancellationToken);
        return item is null || schema!.Access.Level(item.ScopeId) < WorkspaceAccessLevel.Read ? null : ToData(schema, item);
    }

    public async Task<ListItemData?> GetByIdAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        var list = item is null ? null : await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == item.ListId, cancellationToken);
        return list is null ? null : await GetAsync(list.WorkspaceId, list.Id, itemId, cancellationToken);
    }

    public async Task<ListItemPage?> GetRelatedAsync(Guid itemId, int top, Guid? after, CancellationToken cancellationToken)
    {
        if (await GetByIdAsync(itemId, cancellationToken) is not { IsFolder: false })
        {
            return null;
        }

        top = Math.Clamp(top, 1, Api.PageRequest.MaxTop);
        var lists = await GetListsAsync(null, null, cancellationToken);
        var schemas = await loader.LoadManyAsync(lists.Select(l => l.Id).ToArray(), system, cancellationToken);
        var byList = schemas.ToDictionary(s => s.List.Id);
        var listIds = byList.Keys.ToArray();
        var fullLists = schemas.Where(s => s.Access.FullControl).Select(s => s.List.Id).ToArray();
        var scopes = schemas.SelectMany(s => s.Access.Scopes(WorkspaceAccessLevel.Read)).Distinct().ToArray();
        var items = await db.Items.AsNoTracking().Where(i => !i.IsFolder && EF.Parameter(listIds).Contains(i.ListId)
            && (EF.Parameter(fullLists).Contains(i.ListId) || EF.Parameter(scopes).Contains(i.ScopeId))
            && (after == null || i.Id.CompareTo(after.Value) > 0)
            && db.Relations.Any(r => (r.FirstItemId == itemId && r.SecondItemId == i.Id) || (r.SecondItemId == itemId && r.FirstItemId == i.Id)))
            .OrderBy(i => i.Id).Take(top + 1).ToListAsync(cancellationToken);
        return new ListItemPage(items.Take(top).Select(i => ToData(byList[i.ListId], i)).ToList(),
            items.Count > top ? Api.PageRequest.EncodeCursor(items[top - 1].Id) : null);
    }

    public async Task<ListItemResult> RelateAsync(Guid itemId, Guid otherId, bool related, CancellationToken cancellationToken)
    {
        var current = await GetByIdAsync(itemId, cancellationToken);
        var other = await GetByIdAsync(otherId, cancellationToken);
        if (current is null || other is null || current.IsFolder || other.IsFolder)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        if (itemId == otherId)
        {
            return new ListItemResult(ListItemStatus.Invalid, Message: "An item cannot relate to itself.");
        }

        var (source, item, problem) = await LoadForChangeAsync(current.WorkspaceId, current.ListId, itemId, null, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var (target, targetItem, targetProblem) = await LoadForChangeAsync(other.WorkspaceId, other.ListId, otherId, null, cancellationToken);
        if (targetProblem is not null)
        {
            return targetProblem;
        }

        return await writer.RelateItemsAsync(source!, item!, target!, targetItem!, related, cancellationToken)
            ? new ListItemResult(ListItemStatus.Ok, ToData(source!, item!))
            : new ListItemResult(ListItemStatus.VersionMismatch);
    }

    public Task<IReadOnlyList<RelationshipTypeData>> GetRelationshipTypesAsync(CancellationToken ct) => relationshipTypes.ListAsync(ct);

    public Task<RelationshipTypeData> EnsureRelationshipTypeAsync(RelationshipTypeOptions options, CancellationToken ct) => relationshipTypes.EnsureAsync(options, ct);

    public async Task<ItemRelationshipPage?> GetRelationshipsAsync(Guid itemId, string? type, string? direction, int top, Guid? after, CancellationToken ct)
    {
        if (await GetByIdAsync(itemId, ct) is not { IsFolder: false }) return null;
        if (direction is not (null or "both" or "incoming" or "outgoing")) throw new ArgumentException("direction must be both, incoming or outgoing.");
        var typeId = type is null ? (Guid?)null : await relationshipTypes.ResolveAsync(type, false, ct);
        if (type is not null && typeId is null) return new ItemRelationshipPage([], null);
        top = Math.Clamp(top, 1, Api.PageRequest.MaxTop);
        var lists = await GetListsAsync(null, null, ct);
        var schemas = await loader.LoadManyAsync(lists.Select(l => l.Id).ToArray(), system, ct);
        var byList = schemas.ToDictionary(s => s.List.Id);
        var listIds = byList.Keys.ToArray();
        var fullLists = schemas.Where(s => s.Access.FullControl).Select(s => s.List.Id).ToArray();
        var scopes = schemas.SelectMany(s => s.Access.Scopes(WorkspaceAccessLevel.Read)).Distinct().ToArray();
        var edges = await db.Relations.AsNoTracking().Where(r =>
            (r.FirstItemId == itemId || r.SecondItemId == itemId)
            && (typeId == null || r.TypeId == typeId)
            && (direction == null || direction == "both" || (r.Directed && (direction == "outgoing" ? r.FirstItemId == itemId : r.SecondItemId == itemId)))
            && (after == null || r.Id.CompareTo(after.Value) > 0)
            && db.Items.Any(i => i.Id == (r.FirstItemId == itemId ? r.SecondItemId : r.FirstItemId)
                && !i.IsFolder && EF.Parameter(listIds).Contains(i.ListId)
                && (EF.Parameter(fullLists).Contains(i.ListId) || EF.Parameter(scopes).Contains(i.ScopeId))))
            .OrderBy(r => r.Id).Take(top + 1).ToListAsync(ct);
        var next = edges.Count > top ? Api.PageRequest.EncodeCursor(edges[top - 1].Id) : null;
        edges = edges.Take(top).ToList();
        var peerIds = edges.Select(r => r.FirstItemId == itemId ? r.SecondItemId : r.FirstItemId).Distinct().ToArray();
        var peers = await db.Items.AsNoTracking().Where(i => EF.Parameter(peerIds).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var types = await relationshipTypes.DescribeAsync(edges.Select(r => r.TypeId).Where(id => id != Guid.Empty).Distinct().ToArray(), ct);
        return new ItemRelationshipPage(edges.Where(r =>
        {
            var peerId = r.FirstItemId == itemId ? r.SecondItemId : r.FirstItemId;
            return peers.TryGetValue(peerId, out var peer) && byList.TryGetValue(peer.ListId, out var schema)
                && schema.Access.Level(peer.ScopeId) >= WorkspaceAccessLevel.Read;
        }).Select(r =>
        {
            var peer = peers[r.FirstItemId == itemId ? r.SecondItemId : r.FirstItemId];
            return new ItemRelationshipData(r.Id, r.FirstItemId, r.SecondItemId, r.Directed, types.GetValueOrDefault(r.TypeId), ToData(byList[peer.ListId], peer), RelationshipAttributes.Parse(r.Attributes), r.Version);
        }).ToList(), next);
    }

    public async Task<ListItemResult> AddRelationshipAsync(Guid itemId, Guid otherId, RelationshipOptions options, CancellationToken ct)
    {
        RelationshipAttributes.Validate(options.Attributes);
        var current = await GetByIdAsync(itemId, ct);
        var other = await GetByIdAsync(otherId, ct);
        if (current is null || other is null || current.IsFolder || other.IsFolder) return new(ListItemStatus.NotFound);
        if (itemId == otherId) return new(ListItemStatus.Invalid, Message: "An item cannot relate to itself.");
        var (source, item, problem) = await LoadForChangeAsync(current.WorkspaceId, current.ListId, itemId, null, ct);
        if (problem is not null) return problem;
        var (target, peer, targetProblem) = await LoadForChangeAsync(other.WorkspaceId, other.ListId, otherId, null, ct);
        if (targetProblem is not null) return targetProblem;
        var typeId = Guid.Empty;
        RelationshipTypeData? definition = null;
        if (!string.IsNullOrWhiteSpace(options.Type))
        {
            var resolved = await relationshipTypes.ResolveAsync(options.Type, false, ct);
            if (resolved is null)
            {
                if (Guid.TryParse(options.Type, out _)) return new(ListItemStatus.Invalid, Message: "The relationship type is unknown or deprecated.");
                definition = await relationshipTypes.EnsureAsync(new(options.Type, options.Directed ?? false), ct);
                typeId = definition.Id;
            }
            else
            {
                typeId = resolved.Value;
                definition = (await relationshipTypes.DescribeAsync([typeId], ct)).GetValueOrDefault(typeId);
            }
        }
        var directed = options.Directed ?? definition?.Directed ?? false;
        if (definition is not null && directed != definition.Directed)
            return new(ListItemStatus.Invalid, Message: "The direction must match the relationship type.");
        var first = directed || itemId.CompareTo(otherId) < 0 ? itemId : otherId;
        var second = first == itemId ? otherId : itemId;
        var exists = await db.Relations.AnyAsync(r => r.FirstItemId == first && r.SecondItemId == second && r.TypeId == typeId && r.Directed == directed, ct);
        if (!exists && directed && definition is not null)
        {
            if (definition.MaxIncoming is { } maxIn && await db.Relations.CountAsync(r => r.Directed && r.TypeId == typeId && r.SecondItemId == otherId, ct) >= maxIn
                || definition.MaxOutgoing is { } maxOut && await db.Relations.CountAsync(r => r.Directed && r.TypeId == typeId && r.FirstItemId == itemId, ct) >= maxOut)
                return new(ListItemStatus.Rejected, Message: "The relationship type's endpoint limit has been reached.");
        }
        return await writer.RelateItemsAsync(source!, item!, target!, peer!, true, ct, typeId, directed, options.Attributes)
            ? new(ListItemStatus.Ok, ToData(source!, item!)) : new(ListItemStatus.VersionMismatch);
    }

    public async Task<ListItemResult> RemoveRelationshipAsync(Guid itemId, Guid relationshipId, CancellationToken ct)
    {
        var current = await GetByIdAsync(itemId, ct);
        if (current is null) return new(ListItemStatus.NotFound);
        var edge = await db.Relations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == relationshipId && (r.FirstItemId == itemId || r.SecondItemId == itemId), ct);
        if (edge is null) return new(ListItemStatus.NotFound);
        var first = await GetByIdAsync(edge.FirstItemId, ct);
        var second = await GetByIdAsync(edge.SecondItemId, ct);
        if (first is null || second is null) return new(ListItemStatus.NotFound);
        var (source, item, problem) = await LoadForChangeAsync(first.WorkspaceId, first.ListId, first.Id, null, ct);
        if (problem is not null) return problem;
        var (target, peer, targetProblem) = await LoadForChangeAsync(second.WorkspaceId, second.ListId, second.Id, null, ct);
        if (targetProblem is not null) return targetProblem;
        return await writer.RelateItemsAsync(source!, item!, target!, peer!, false, ct, edge.TypeId, edge.Directed)
            ? new(ListItemStatus.Ok, ToData(source!, item!)) : new(ListItemStatus.VersionMismatch);
    }

    public async Task<ListItemResult> MoveToAsync(Guid itemId, Guid workspaceId, Guid listId, Guid? folderId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var current = await GetByIdAsync(itemId, cancellationToken);
        if (current is null)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        var (source, item, problem) = await LoadForChangeAsync(current.WorkspaceId, current.ListId, itemId, expectedVersion, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var destination = await LoadAsync(workspaceId, listId, cancellationToken);
        if (destination is null)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        try
        {
            return ToResult(destination, await writer.MoveAcrossListsAsync(source!, destination, item!, folderId, cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ListItemResult(ListItemStatus.VersionMismatch);
        }
    }

    public async Task<(IReadOnlyList<ListItemData> Items, string? Error)> QueryAsync(
        Guid workspaceId, Guid listId, ListItemQuery query, CancellationToken cancellationToken)
    {
        var (page, error) = await QueryPageAsync(workspaceId, listId, query, cancellationToken);
        return (page?.Items ?? [], error);
    }

    public async Task<(ListItemPage? Page, string? Error)> QueryPageAsync(
        Guid workspaceId, Guid listId, ListItemQuery query, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return (null, "The list was not found.");
        }

        var (items, next, error) = await runner.ListPageAsync(schema, query.Filter, query.OrderBy, query.Top, query.SkipToken, null, false, cancellationToken);
        return error is not null ? (null, error) : (new ListItemPage(items!.Select(i => ToData(schema, i)).ToList(), next), null);
    }

    public async Task<(ListItemPage? Page, string? Error)> ListChildrenAsync(
        Guid workspaceId, Guid listId, Guid? folderId, ListItemQuery query, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return (null, "The list was not found.");
        }

        if (folderId is { } id && await GetAsync(workspaceId, listId, id, cancellationToken) is not { IsFolder: true })
        {
            return (null, "The folder was not found (or you cannot read it).");
        }

        var (items, next, error) = await runner.ListPageAsync(schema, query.Filter, query.OrderBy, query.Top, query.SkipToken, folderId, true, cancellationToken);
        return error is not null ? (null, error) : (new ListItemPage(items!.Select(i => ToData(schema, i)).ToList(), next), null);
    }

    public async Task<(IReadOnlyList<ListQueryResult> Results, string? Error)> QueryAsync(
        IReadOnlyList<ListData> lists, ListItemQuery query, CancellationToken cancellationToken)
    {
        // One pass for every list's schema and access, then one query per group of lists that translate the same way
        // (lists from one template are one group), instead of all of it per list (ADR-0035, issue 0009).
        var schemas = await loader.LoadManyAsync([.. lists.Select(l => l.Id)], system, cancellationToken);
        var byId = schemas.ToDictionary(s => s.List.Id);
        var found = lists.ToDictionary(l => l.Id, _ => new List<ListItemData>());
        var top = Math.Clamp(query.Top, 1, ListItemQuery.MaxTop);
        foreach (var group in schemas.GroupBy(ItemQueryRunner.QueryShape))
        {
            var (items, translator, order, error) = await runner.MatchingManyAsync([.. group], query.Filter, query.OrderBy, i => !i.IsFolder, cancellationToken);
            if (error is not null)
            {
                return ([], error);
            }

            var ordered = order is null ? items!.OrderBy(i => i.Id) : translator!.OrderBy(items!, order);
            foreach (var item in await ordered.Take(top).ToListAsync(cancellationToken))
            {
                found[item.ListId].Add(ToData(byId[item.ListId], item));
            }
        }

        return ([.. lists.Where(l => byId.ContainsKey(l.Id)).Select(l => new ListQueryResult(l, found[l.Id]))], null);
    }

    public Task<ListItemResult> CreateAsync(Guid workspaceId, Guid listId, JsonObject fields, Guid? contentTypeId, CancellationToken cancellationToken) =>
        CreateAsync(workspaceId, listId, fields, contentTypeId, null, cancellationToken);

    public async Task<ListItemResult> CreateAsync(
        Guid workspaceId, Guid listId, JsonObject fields, Guid? contentTypeId, Guid? parentId, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        return ToResult(schema, await writer.CreateAsync(schema, contentTypeId, parentId, isFolder: false, Element(fields), cancellationToken));
    }

    public async Task<ListItemResult> CreateFolderAsync(
        Guid workspaceId, Guid listId, string title, Guid? parentId, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        return ToResult(schema, await writer.CreateAsync(schema, null, parentId, isFolder: true, Element(new JsonObject { ["title"] = title }), cancellationToken));
    }

    public Task<ListItemResult> CreateAsync(
        Guid workspaceId, Guid listId, Guid itemId, JsonObject fields, Guid? contentTypeId, CancellationToken cancellationToken) =>
        CreateAsync(workspaceId, listId, itemId, fields, contentTypeId, null, cancellationToken);

    public async Task<ListItemResult> CreateAsync(
        Guid workspaceId, Guid listId, Guid itemId, JsonObject fields, Guid? contentTypeId, Guid? parentId, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        var existing = await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (existing is not null)
        {
            return existing.ListId == listId && existing.DeletedAt is null
                ? new ListItemResult(ListItemStatus.Ok, ToData(schema, existing))
                : new ListItemResult(ListItemStatus.Rejected, Message: "An item with this id exists in another list or was deleted.");
        }

        return ToResult(schema, await writer.CreateAsync(schema, contentTypeId, parentId, isFolder: false, Element(fields), itemId, cancellationToken));
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

    public async Task<(Guid? FolderId, ListItemResult? Problem)> EnsureFolderAsync(
        Guid workspaceId, Guid listId, IReadOnlyList<string> path, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        if (schema is null)
        {
            return (null, new ListItemResult(ListItemStatus.NotFound));
        }

        Guid? parentId = null;
        foreach (var segment in path.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var parent = parentId;
            var existing = await db.Items.AsNoTracking()
                .Where(i => i.ListId == listId && i.IsFolder && i.ParentId == parent && i.Title == segment)
                .OrderBy(i => i.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                if (schema.Access.Level(existing.ScopeId) < WorkspaceAccessLevel.Read)
                {
                    return (null, new ListItemResult(ListItemStatus.Forbidden));
                }

                parentId = existing.Id;
                continue;
            }

            var created = await writer.CreateAsync(schema, null, parentId, isFolder: true, Element(new JsonObject { ["title"] = segment }), cancellationToken);
            if (created.Item is null)
            {
                return (null, ToResult(schema, created));
            }

            parentId = created.Item.Id;
        }

        return (parentId, null);
    }

    public async Task<ListItemResult> MoveAsync(Guid workspaceId, Guid listId, Guid itemId, Guid? folderId, CancellationToken cancellationToken)
    {
        var (schema, item, problem) = await LoadForChangeAsync(workspaceId, listId, itemId, null, cancellationToken);
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
            return ToResult(schema!, await writer.UpdateAsync(schema!, item, null, Optional<Guid?>.Of(folderId), null, cancellationToken));
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

    public async Task<ListItemResult> RestoreAsync(Guid workspaceId, Guid listId, Guid itemId, uint? expectedVersion, CancellationToken cancellationToken)
    {
        var schema = await LoadAsync(workspaceId, listId, cancellationToken);
        var item = schema is null ? null : await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete])
            .FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, cancellationToken);
        var level = item is null ? WorkspaceAccessLevel.None : schema!.Access.Level(item.ScopeId);
        if (level < WorkspaceAccessLevel.Read)
        {
            return new ListItemResult(ListItemStatus.NotFound);
        }

        if (level < WorkspaceAccessLevel.Contribute)
        {
            return new ListItemResult(ListItemStatus.Forbidden);
        }

        if (expectedVersion is { } version && version != item!.Version)
        {
            return new ListItemResult(ListItemStatus.VersionMismatch);
        }

        try
        {
            if (item!.DeletedAt is not null)
            {
                await writer.RestoreAsync(schema!, item, cancellationToken);
            }

            return new ListItemResult(ListItemStatus.Ok, ToData(schema!, item));
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

    private static ListFieldInfo FieldInfo(FieldDefinition field) => new(
        field.Name, field.DisplayName, field.Type, field.Required, field.AllowMultiple, field.Description,
        field.MaxLength, field.Minimum, field.Maximum, field.Choices.Count > 0 ? field.Choices : null);

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
