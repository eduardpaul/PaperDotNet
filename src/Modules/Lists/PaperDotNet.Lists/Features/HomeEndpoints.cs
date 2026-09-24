using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
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

    private static async Task<Ok<HomeResponse>> GetHomeAsync(IWorkspaceAccess workspaces, ListsDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await EnsureHomeAsync(workspaces, db, ct));

    /// <summary>The current user's Home workspace with its libraries, created on first use.</summary>
    internal static async Task<HomeResponse> EnsureHomeAsync(IWorkspaceAccess workspaces, ListsDbContext db, CancellationToken ct)
    {
        var workspaceId = await workspaces.EnsurePersonalWorkspaceAsync(ct);
        var documents = await EnsureLibraryAsync(db, workspaceId, ListDefinition.HomeDocumentsKey, "Documents", "Your documents.", ct);
        var inbox = await EnsureLibraryAsync(db, workspaceId, ListDefinition.HomeInboxKey, "Inbox", "New uploads land here for triage.", ct);
        return new HomeResponse(workspaceId, documents, inbox);
    }

    private static async Task<Guid> EnsureLibraryAsync(ListsDbContext db, Guid workspaceId, string key, string name, string description, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.Lists.Where(l => l.WorkspaceId == workspaceId && l.SystemKey == key).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
            if (existing is { } id)
            {
                return id;
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
                ContentTypeIds = [await ListEndpoints.EnsureItemContentTypeAsync(db, ct)],
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
}
