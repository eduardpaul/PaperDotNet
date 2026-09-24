using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>A list with its content types and the effective set of fields (union, by name).</summary>
internal sealed class ListSchema
{
    public ListSchema(ListDefinition list, IReadOnlyList<ContentType> contentTypes, WorkspaceAccessLevel permission)
    {
        List = list;
        Permission = permission;
        ContentTypes = list.ContentTypeIds
            .Select(id => contentTypes.FirstOrDefault(c => c.Id == id))
            .OfType<ContentType>()
            .ToList();
        Fields = ContentTypes
            .SelectMany(c => c.Fields)
            .GroupBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public ListDefinition List { get; }

    public WorkspaceAccessLevel Permission { get; }

    public IReadOnlyList<ContentType> ContentTypes { get; }

    public ContentType DefaultContentType => ContentTypes[0];

    public IReadOnlyDictionary<string, FieldDefinition> Fields { get; }

    public ContentType? FindContentType(Guid id) => ContentTypes.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Returns a conflict message when adding <paramref name="contentType"/> would give an
    /// existing field name a different type or multiplicity.
    /// </summary>
    public static string? FindConflict(IEnumerable<FieldDefinition> existing, ContentType contentType)
    {
        var byName = existing.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var clash = contentType.Fields.FirstOrDefault(f =>
            byName.TryGetValue(f.Name, out var other) && (other.Type != f.Type || other.AllowMultiple != f.AllowMultiple));
        return clash is null ? null : $"Field '{clash.Name}' already exists in the list with a different type.";
    }
}

/// <summary>Loads a list schema, enforcing workspace visibility (404 when not visible).</summary>
internal sealed class ListSchemaLoader(ListsDbContext db, IWorkspaceAccess workspaces)
{
    public async Task<ListSchema?> LoadAsync(Guid workspaceId, Guid listId, CancellationToken ct, bool tracking = false)
    {
        var permission = await workspaces.GetPermissionAsync(workspaceId, ct);
        if (permission == WorkspaceAccessLevel.None)
        {
            return null;
        }

        var lists = tracking ? db.Lists : db.Lists.AsNoTracking();
        var list = await lists.FirstOrDefaultAsync(l => l.Id == listId && l.WorkspaceId == workspaceId, ct);
        if (list is null)
        {
            return null;
        }

        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => list.ContentTypeIds.Contains(c.Id)).ToListAsync(ct);
        return new ListSchema(list, contentTypes, permission);
    }
}
