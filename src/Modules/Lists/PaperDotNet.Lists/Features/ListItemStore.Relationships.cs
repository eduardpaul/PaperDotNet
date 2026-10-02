using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

internal sealed partial class ListItemStore
{
    public async Task<ItemRelationshipData?> GetRelationshipAsync(Guid itemId, Guid relationshipId, CancellationToken ct)
    {
        if (await GetByIdAsync(itemId, ct) is null) return null;
        var edge = await db.Relations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == relationshipId && (r.FirstItemId == itemId || r.SecondItemId == itemId), ct);
        if (edge is null) return null;
        var peer = await GetByIdAsync(edge.FirstItemId == itemId ? edge.SecondItemId : edge.FirstItemId, ct);
        if (peer is null) return null;
        var types = await relationshipTypes.DescribeAsync(edge.TypeId == Guid.Empty ? [] : [edge.TypeId], ct);
        return new ItemRelationshipData(edge.Id, edge.FirstItemId, edge.SecondItemId, edge.Directed, types.GetValueOrDefault(edge.TypeId), peer, RelationshipAttributes.Parse(edge.Attributes), edge.Version);
    }

    public async Task<ListItemResult> UpdateRelationshipAsync(Guid itemId, Guid relationshipId, JsonObject attributes, uint expectedVersion, CancellationToken ct)
    {
        RelationshipAttributes.Validate(attributes);
        if (await GetByIdAsync(itemId, ct) is null) return new(ListItemStatus.NotFound);
        var edge = await db.Relations.FirstOrDefaultAsync(r => r.Id == relationshipId && (r.FirstItemId == itemId || r.SecondItemId == itemId), ct);
        if (edge is null) return new(ListItemStatus.NotFound);
        var first = await GetByIdAsync(edge.FirstItemId, ct);
        var second = await GetByIdAsync(edge.SecondItemId, ct);
        if (first is null || second is null) return new(ListItemStatus.NotFound);
        var (source, item, problem) = await LoadForChangeAsync(first.WorkspaceId, first.ListId, first.Id, null, ct);
        if (problem is not null) return problem;
        var (target, peer, otherProblem) = await LoadForChangeAsync(second.WorkspaceId, second.ListId, second.Id, null, ct);
        if (otherProblem is not null) return otherProblem;
        if (edge.Version != expectedVersion) return new(ListItemStatus.VersionMismatch);
        return await writer.UpdateRelationshipAsync(edge, attributes, source!, item!, target!, peer!, ct)
            ? new(ListItemStatus.Ok) : new(ListItemStatus.VersionMismatch);
    }

    public async Task<WorkspaceRelationshipPage?> QueryRelationshipsAsync(Guid workspaceId, string? type, bool? directed, string? filter, int top, Guid? after, CancellationToken ct)
    {
        var lists = await GetListsAsync(null, null, ct);
        if (system)
        {
            if (!(await workspaces.GetNamesAsync([workspaceId], ct)).ContainsKey(workspaceId)) return null;
        }
        else if (!lists.Any(l => l.WorkspaceId == workspaceId) && await workspaces.GetPermissionAsync(workspaceId, ct) < WorkspaceAccessLevel.Read) return null;
        var schemas = await loader.LoadManyAsync(lists.Select(l => l.Id).ToArray(), system, ct);
        var byList = schemas.ToDictionary(s => s.List.Id);
        var listIds = byList.Keys.ToArray();
        var workspaceLists = schemas.Where(s => s.List.WorkspaceId == workspaceId).Select(s => s.List.Id).ToArray();
        var fullLists = schemas.Where(s => s.Access.FullControl).Select(s => s.List.Id).ToArray();
        var scopes = schemas.SelectMany(s => s.Access.Scopes(WorkspaceAccessLevel.Read)).Distinct().ToArray();
        var readable = db.Items.AsNoTracking().Where(i => !i.IsFolder && EF.Parameter(listIds).Contains(i.ListId)
            && (EF.Parameter(fullLists).Contains(i.ListId) || EF.Parameter(scopes).Contains(i.ScopeId)));
        var edges = db.Relations.AsNoTracking().Where(r => readable.Any(i => i.Id == r.FirstItemId) && readable.Any(i => i.Id == r.SecondItemId)
            && readable.Any(i => (i.Id == r.FirstItemId || i.Id == r.SecondItemId) && EF.Parameter(workspaceLists).Contains(i.ListId)));
        if (type is not null)
        {
            var typeId = await relationshipTypes.ResolveAsync(type, false, ct);
            if (typeId is null) return new WorkspaceRelationshipPage([], null);
            edges = edges.Where(r => r.TypeId == typeId);
        }
        if (directed is not null) edges = edges.Where(r => r.Directed == directed);
        if (!string.IsNullOrWhiteSpace(filter)) edges = edges.Where(RelationshipQueryTranslator.Parse(filter));
        if (after is not null) edges = edges.Where(r => r.Id.CompareTo(after.Value) > 0);
        top = Math.Clamp(top, 1, Api.PageRequest.MaxTop);
        var page = await edges.OrderBy(r => r.Id).Take(top + 1).ToListAsync(ct);
        var next = page.Count > top ? Api.PageRequest.EncodeCursor(page[top - 1].Id) : null;
        page = page.Take(top).ToList();
        var ids = page.SelectMany(r => new[] { r.FirstItemId, r.SecondItemId }).Distinct().ToArray();
        var items = await readable.Where(i => EF.Parameter(ids).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var types = await relationshipTypes.DescribeAsync(page.Select(r => r.TypeId).Where(id => id != Guid.Empty).Distinct().ToArray(), ct);
        bool StillVisible(ItemRelation edge) => items.TryGetValue(edge.FirstItemId, out var first) && items.TryGetValue(edge.SecondItemId, out var second)
            && (workspaceLists.Contains(first.ListId) || workspaceLists.Contains(second.ListId));
        return new WorkspaceRelationshipPage(page.Where(StillVisible).Select(r => new WorkspaceRelationshipData(r.Id, r.Directed,
            types.GetValueOrDefault(r.TypeId), RelationshipAttributes.Parse(r.Attributes), r.Version,
            ToData(byList[items[r.FirstItemId].ListId], items[r.FirstItemId]), ToData(byList[items[r.SecondItemId].ListId], items[r.SecondItemId]))).ToList(), next);
    }
}
