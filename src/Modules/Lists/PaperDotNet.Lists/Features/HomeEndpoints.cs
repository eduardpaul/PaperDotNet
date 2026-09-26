using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>The user's personal workspace with its Documents and Inbox libraries.</summary>
public sealed record HomeResponse(Guid WorkspaceId, Guid DocumentsListId, Guid InboxListId);

/// <summary>
/// Home and Inbox (LST-07): every user has a personal workspace ("Home") with a
/// Documents library and an Inbox library where new uploads land for triage.
/// Created on first use.
/// </summary>
internal static class HomeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapV1Group("me", "Me")
            .MapGet("/home", GetHomeAsync)
            .RequireScope(ListScopes.Read)
            .WithName("GetHome");

    private static async Task<Ok<HomeResponse>> GetHomeAsync(
        IWorkspaceAccess workspaces, ListsDbContext db, ContentTypeProvisioner contentTypes, ListTemplateRegistry templates, CancellationToken ct) =>
        TypedResults.Ok(await EnsureHomeAsync(workspaces, db, contentTypes, templates, ct));

    /// <summary>The current user's Home workspace with its libraries, created on first use.</summary>
    internal static async Task<HomeResponse> EnsureHomeAsync(
        IWorkspaceAccess workspaces, ListsDbContext db, ContentTypeProvisioner contentTypes, ListTemplateRegistry templates, CancellationToken ct)
    {
        var workspaceId = await workspaces.EnsurePersonalWorkspaceAsync(ct);
        var documents = await EnsureLibraryAsync(db, contentTypes, templates, workspaceId, ListDefinition.HomeDocumentsKey, "Documents", "Your documents.", ct);
        var inbox = await EnsureLibraryAsync(db, contentTypes, templates, workspaceId, ListDefinition.HomeInboxKey, "Inbox", "New uploads land here for triage.", ct);
        return new HomeResponse(workspaceId, documents, inbox);
    }

    private static async Task<Guid> EnsureLibraryAsync(
        ListsDbContext db, ContentTypeProvisioner contentTypes, ListTemplateRegistry templates, Guid workspaceId, string key, string name, string description, CancellationToken ct)
    {
        var document = await contentTypes.EnsureAsync(templates.FindContentType(BuiltInTemplates.DocumentKey)!, ct);
        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.Lists.FirstOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.SystemKey == key, ct);
            if (existing is not null)
            {
                await UseDocumentContentTypeAsync(db, existing, document.Id, ct);
                return existing.Id;
            }

            var list = new ListDefinition
            {
                Id = Ids.New(),
                WorkspaceId = workspaceId,
                Name = name,
                Description = description,
                Kind = ListKind.Library,
                Versioning = ListVersioning.Major,
                SystemKey = key,
                TemplateKey = BuiltInTemplates.DocumentsListKey,
                ContentTypeIds = [document.Id],
            };
            db.Lists.Add(list);
            try
            {
                await db.SaveChangesAsync(ct);
                return list.Id;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Created concurrently (unique index on workspace + key); read it back.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Home libraries use the document content type (description and keywords). Libraries created earlier with the
    /// generic Item type are switched, and their items move with them.
    /// </summary>
    private static async Task UseDocumentContentTypeAsync(ListsDbContext db, ListDefinition list, Guid documentId, CancellationToken ct)
    {
        var genericId = await db.ContentTypes.AsNoTracking()
            .Where(c => c.IsBuiltIn && c.Key == null && c.Name == ContentType.ItemName)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        if (list.TemplateKey == BuiltInTemplates.DocumentsListKey && list.ContentTypeIds.FirstOrDefault() == documentId
            && (genericId is null || !list.ContentTypeIds.Contains(genericId.Value)))
        {
            return;
        }

        if (genericId is { } generic && list.ContentTypeIds.Contains(generic))
        {
            var items = db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(i => i.ListId == list.Id && i.ContentTypeId == generic);
            await items.ExecuteUpdateAsync(s => s.SetProperty(i => i.ContentTypeId, documentId), ct);
            await db.ItemVersions.Where(v => v.ListId == list.Id && v.ContentTypeId == generic)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ContentTypeId, documentId), ct);
        }

        list.TemplateKey ??= BuiltInTemplates.DocumentsListKey;
        list.ContentTypeIds = [documentId, .. list.ContentTypeIds.Where(id => id != documentId && id != genericId)];
        await db.SaveChangesAsync(ct);
    }
}
