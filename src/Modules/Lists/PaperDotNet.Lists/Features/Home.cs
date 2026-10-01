using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Templates;
using PaperDotNet.Messaging;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>The user's personal workspace with its Documents and Inbox libraries.</summary>
public sealed record HomeResponse(Guid WorkspaceId, Guid DocumentsListId, Guid InboxListId);

/// <summary>
/// Home and Inbox (LST-07): every user has a personal workspace ("Home") with a Documents library and an Inbox library
/// where new uploads land for triage, both of the document content type. Created on first use.
/// </summary>
internal sealed class HomeLibraries(
    IWorkspaceAccess workspaces, ListsDbContext db, ContentTypeProvisioner contentTypes, ListTemplateRegistry templates, IOutbox outbox)
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGroup("/v1.0/me").WithTags("Me")
            .MapGet("/home", GetAsync)
            .RequireScope(ListScopes.Read)
            .WithName("GetHome")
            .WithDescription("The caller's Home workspace with its Documents and Inbox libraries, created on first use.");

    private static async Task<Ok<HomeResponse>> GetAsync(Caller caller, HomeLibraries home, CancellationToken cancellationToken)
    {
        var data = await home.EnsureAsync(caller.TenantId, caller.UserId, cancellationToken);
        return TypedResults.Ok(new HomeResponse(data.WorkspaceId, data.DocumentsListId, data.InboxListId));
    }

    public async Task<HomeData> EnsureAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var workspaceId = await workspaces.EnsurePersonalWorkspaceAsync(tenantId, userId, ct);
        var documents = await EnsureLibraryAsync(tenantId, userId, workspaceId, ListDefinition.HomeDocumentsKey, "Documents", "Your documents.", ct);
        var inbox = await EnsureLibraryAsync(tenantId, userId, workspaceId, ListDefinition.HomeInboxKey, "Inbox", "New uploads land here for triage.", ct);
        return new HomeData(workspaceId, documents, inbox);
    }

    private async Task<Guid> EnsureLibraryAsync(Guid tenantId, Guid userId, Guid workspaceId, string key, string name, string description, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await FindAsync(db, tenantId, workspaceId, key, ct) is { } existing)
            {
                return existing;
            }

            var template = templates.FindList(BuiltInTemplates.DocumentsListKey)!;
            var document = await contentTypes.EnsureAsync(tenantId, templates.FindContentType(BuiltInTemplates.DocumentKey)!, ct);
            var list = new ListDefinition
            {
                Id = Ids.New(),
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                Name = name,
                Description = description,
                Kind = ListKinds.Library,
                Versioning = ListVersionings.Major,
                SystemKey = key,
                TemplateKey = template.Key,
                AllowFolders = template.AllowFolders,
                ContentTypeIds = ListsJsonText.Ids([document.Id]),
            };
            db.Lists.Add(list);
            db.Views.AddRange(ListTemplateEndpoints.Views(list, template));
            try
            {
                await outbox.SaveChangesAsync(db, [new ListCreated { TenantId = tenantId, UserId = userId, WorkspaceId = workspaceId, ListId = list.Id, Name = list.Name }], ct);
                return list.Id;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Created at the same moment (unique workspace + key): read it back.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static Task<Guid?> FindAsync(ListsDbContext database, Guid tenantId, Guid workspaceId, string systemKey, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var workspace = workspaceId;
        var key = systemKey;
        var ct = cancellationToken;
        return context.Lists.Where(l => l.TenantId == tenant && l.WorkspaceId == workspace && l.SystemKey == key && l.DeletedAt == null)
            .Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
    }
}
