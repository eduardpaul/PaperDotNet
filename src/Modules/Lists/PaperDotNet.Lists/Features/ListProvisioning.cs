using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Messaging;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Content types of one template run (created, updated or planned), shared by the Lists sections.</summary>
internal sealed class ListsTemplateState
{
    private const string Key = "lists.contentTypes";

    public Dictionary<string, ContentType> ByName { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, ContentType> ByKey { get; } = new(StringComparer.Ordinal);

    public static ListsTemplateState Of(TemplateContext context)
    {
        if (!context.Items.TryGetValue(Key, out var state))
        {
            state = new ListsTemplateState();
            context.Items[Key] = state;
        }

        return (ListsTemplateState)state;
    }

    public void Add(ContentType contentType)
    {
        ByName[contentType.Name] = contentType;
        if (contentType.Key is { } key)
        {
            ByKey[key] = contentType;
        }
    }
}

/// <summary>Reference resolution shared by the Lists sections (templates use names, never ids).</summary>
internal sealed class ListTemplateLookups(ListsDbContext db, IWorkspaceAccess workspaces, ITermStore terms)
{
    /// <summary><c>Group/Set</c> → term set id: from this template run, else the tenant.</summary>
    public async Task<Guid?> TermSetAsync(string path, TemplateContext context, CancellationToken ct)
    {
        if (context.Resolve(TemplateKinds.TermSet, path) is { } planned)
        {
            return planned;
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 ? null : await terms.FindTermSetAsync(path[..slash], path[(slash + 1)..], ct);
    }

    /// <summary><c>Workspace/List</c> → list id: from this template run, else the tenant.</summary>
    public async Task<Guid?> ListAsync(string path, TemplateContext context, CancellationToken ct)
    {
        if (context.Resolve(TemplateKinds.List, path) is { } planned)
        {
            return planned;
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || await workspaces.FindSharedAsync(path[..slash], ct) is not { } workspaceId)
        {
            return null;
        }

        var name = path[(slash + 1)..];
        return await db.Lists.Where(l => l.WorkspaceId == workspaceId && l.Name == name).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<string?> ListPathAsync(Guid listId, CancellationToken ct)
    {
        var list = await db.Lists.AsNoTracking().Where(l => l.Id == listId).Select(l => new { l.WorkspaceId, l.Name }).FirstOrDefaultAsync(ct);
        return list is null ? null
            : (await workspaces.GetNamesAsync([list.WorkspaceId], ct)).TryGetValue(list.WorkspaceId, out var workspace) ? $"{workspace}/{list.Name}" : null;
    }
}

