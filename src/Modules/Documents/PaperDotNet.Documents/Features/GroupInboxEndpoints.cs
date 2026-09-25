using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

public sealed record GroupInboxRequest([property: Required] Guid WorkspaceId, [property: Required] Guid ListId);

public sealed record GroupInboxResponse(Guid GroupId, string? GroupName, Guid WorkspaceId, Guid ListId, string? ListName);

/// <summary>An inbox the caller can upload into: <c>personal</c> (Home/Inbox) or <c>group</c>.</summary>
public sealed record InboxResponse(string Kind, Guid WorkspaceId, Guid ListId, string ListName, Guid? GroupId, string? GroupName);

/// <summary>
/// Group inboxes (DOC-16): a library designated as a group's inbox. Members upload into it through
/// <c>/v1.0/groups/{id}/inbox/documents</c> (with their own access to the library) and find it in <c>/v1.0/me/inboxes</c>.
/// </summary>
internal static class GroupInboxEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var limit = endpoints.ServiceProvider.GetRequiredService<IOptions<DocumentsOptions>>().Value.MaxFileSize + (1024 * 1024);
        var inbox = endpoints.MapV1Group("groups/{id:guid}/inbox", "Documents");
        inbox.MapGet("", GetAsync).RequireScope(DocumentScopes.Read).WithName("GetGroupInbox");
        inbox.MapPut("", SetAsync).RequireScope(DocumentScopes.Write).WithName("SetGroupInbox");
        inbox.MapDelete("", RemoveAsync).RequireScope(DocumentScopes.Write).WithName("RemoveGroupInbox");
        inbox.MapPost("/documents", UploadAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("UploadToGroupInbox")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit);

        endpoints.MapV1Group("me", "Documents").MapGet("/inboxes", MyInboxesAsync).RequireScope(DocumentScopes.Read).WithName("ListMyInboxes");
    }

    private static async Task<Results<Ok<GroupInboxResponse>, ProblemHttpResult>> GetAsync(
        [FromRoute(Name = "id")] Guid groupId, DocumentsDbContext db, IUserDirectory directory, IListItemStore items, CancellationToken ct)
    {
        var inbox = await db.GroupInboxes.AsNoTracking().FirstOrDefaultAsync(g => g.GroupId == groupId, ct);
        var list = inbox is null ? null : await items.GetListAsync(inbox.WorkspaceId, inbox.ListId, ct);
        if (inbox is null || list is null)
        {
            return ApiErrors.NotFound("The group has no inbox you can see.");
        }

        var names = await directory.GetGroupNamesAsync([groupId], ct);
        return TypedResults.Ok(new GroupInboxResponse(groupId, names.GetValueOrDefault(groupId), inbox.WorkspaceId, inbox.ListId, list.Name));
    }

    /// <summary>Makes a library the group's inbox (needs Manage on the library). It replaces an earlier one.</summary>
    private static async Task<Results<Ok<GroupInboxResponse>, ValidationProblem, ProblemHttpResult>> SetAsync(
        [FromRoute(Name = "id")] Guid groupId, GroupInboxRequest request, DocumentsDbContext db, IUserDirectory directory, IListItemStore items, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        if (!await directory.GroupExistsAsync(groupId, ct))
        {
            return ApiErrors.NotFound("The group was not found.");
        }

        var list = await items.GetListAsync(request.WorkspaceId, request.ListId, ct);
        if (list is null)
        {
            return ApiErrors.NotFound("The library was not found.");
        }

        if (!list.IsLibrary)
        {
            return ApiErrors.Problem(StatusCodes.Status400BadRequest, "notALibrary", "A group inbox must be a library.");
        }

        var existing = await db.GroupInboxes.FirstOrDefaultAsync(g => g.GroupId == groupId, ct);
        if (list.Access < WorkspaceAccessLevel.Manage || (existing is not null && !await CanManageAsync(items, existing, ct)))
        {
            return DocumentService.Forbidden();
        }

        if (existing is null)
        {
            existing = new GroupInbox { Id = Ids.New(), GroupId = groupId };
            db.GroupInboxes.Add(existing);
        }

        existing.WorkspaceId = request.WorkspaceId;
        existing.ListId = request.ListId;
        await db.SaveChangesAsync(ct);
        var names = await directory.GetGroupNamesAsync([groupId], ct);
        return TypedResults.Ok(new GroupInboxResponse(groupId, names.GetValueOrDefault(groupId), existing.WorkspaceId, existing.ListId, list.Name));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        [FromRoute(Name = "id")] Guid groupId, DocumentsDbContext db, IListItemStore items, CancellationToken ct)
    {
        var existing = await db.GroupInboxes.FirstOrDefaultAsync(g => g.GroupId == groupId, ct);
        if (existing is null)
        {
            return ApiErrors.NotFound();
        }

        if (!await CanManageAsync(items, existing, ct))
        {
            return DocumentService.Forbidden();
        }

        db.GroupInboxes.Remove(existing);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Uploads into the group's inbox. Only members may; the upload needs Contribute on the library as usual.</summary>
    private static async Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        [FromRoute(Name = "id")] Guid groupId, IFormFile? file, [FromForm] string? title, [FromForm] string? languages, ICurrentUser user,
        IUserDirectory directory, DocumentsDbContext db, DocumentService documents, CancellationToken ct)
    {
        var inbox = await db.GroupInboxes.AsNoTracking().FirstOrDefaultAsync(g => g.GroupId == groupId, ct);
        if (inbox is null || user.UserId is not { } userId || !(await directory.GetGroupIdsAsync(userId, ct)).Contains(groupId))
        {
            return ApiErrors.NotFound("The group has no inbox, or you are not a member.");
        }

        return await documents.UploadAsync(inbox.WorkspaceId, inbox.ListId, file, title, null, languages, ct);
    }

    /// <summary>The caller's inboxes: the personal one and those of their groups that they can read.</summary>
    private static async Task<Ok<List<InboxResponse>>> MyInboxesAsync(
        ICurrentUser user, IUserDirectory directory, DocumentsDbContext db, IListItemStore items, CancellationToken ct)
    {
        var result = new List<InboxResponse>();
        if (user.UserId is not { } userId)
        {
            return TypedResults.Ok(result);
        }

        var home = await items.EnsureHomeAsync(ct);
        result.Add(new InboxResponse("personal", home.WorkspaceId, home.InboxListId, "Inbox", null, null));
        var groupIds = await directory.GetGroupIdsAsync(userId, ct);
        var inboxes = await db.GroupInboxes.AsNoTracking().Where(g => groupIds.Contains(g.GroupId)).ToListAsync(ct);
        var names = await directory.GetGroupNamesAsync([.. inboxes.Select(g => g.GroupId)], ct);
        foreach (var inbox in inboxes.OrderBy(g => names.GetValueOrDefault(g.GroupId), StringComparer.OrdinalIgnoreCase))
        {
            if (await items.GetListAsync(inbox.WorkspaceId, inbox.ListId, ct) is { } list)
            {
                result.Add(new InboxResponse("group", inbox.WorkspaceId, inbox.ListId, list.Name, inbox.GroupId, names.GetValueOrDefault(inbox.GroupId)));
            }
        }

        return TypedResults.Ok(result);
    }

    /// <summary>Manage on the current inbox library, or the library no longer exists.</summary>
    private static async Task<bool> CanManageAsync(IListItemStore items, GroupInbox inbox, CancellationToken ct) =>
        await items.GetListAsync(inbox.WorkspaceId, inbox.ListId, ct) is { } list
            ? list.Access >= WorkspaceAccessLevel.Manage
            : await items.AsSystem().GetListAsync(inbox.WorkspaceId, inbox.ListId, ct) is null;
}

/// <summary>Removes the inbox of a deleted group (IAM-14).</summary>
internal sealed class DeletedGroupInbox(DocumentsDbContext db) : IEventSubscriber<PrincipalDeleted>
{
    public async Task HandleAsync(PrincipalDeleted integrationEvent, CancellationToken cancellationToken)
    {
        if (integrationEvent.IsGroup)
        {
            await db.GroupInboxes.Where(g => g.GroupId == integrationEvent.PrincipalId).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
