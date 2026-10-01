using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

public sealed record GroupInboxRequest(Guid WorkspaceId, Guid ListId);

public sealed record GroupInboxResponse(Guid GroupId, string? GroupName, Guid WorkspaceId, Guid ListId, string? ListName);

/// <summary>An inbox the caller can upload into: <c>personal</c> (Home/Inbox) or <c>group</c>.</summary>
public sealed record InboxResponse(string Kind, Guid WorkspaceId, Guid ListId, string ListName, Guid? GroupId, string? GroupName);

/// <summary>
/// The caller's inboxes (LST-07, DOC-16): uploads into the personal Inbox library of Home (<c>/v1.0/me/inbox/documents</c>)
/// and group inboxes, libraries designated as a group's inbox. Members upload into one through
/// <c>/v1.0/groups/{id}/inbox/documents</c> (with their own access to the library) and find it in <c>/v1.0/me/inboxes</c>.
/// </summary>
internal static class GroupInboxEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var limit = endpoints.ServiceProvider.GetRequiredService<IOptions<DocumentsOptions>>().Value.MaxFileSize + (1024 * 1024);
        var inbox = endpoints.MapGroup("/v1.0/groups/{id:guid}/inbox").WithTags("Documents");
        inbox.MapGet("", GetAsync).RequireScope(DocumentScopes.Read).WithName("GetGroupInbox");
        inbox.MapPut("", SetAsync).RequireScope(DocumentScopes.Write).WithName("SetGroupInbox")
            .WithDescription("Makes a library the group's inbox (needs Manage on the library). It replaces an earlier one.");
        inbox.MapDelete("", RemoveAsync).RequireScope(DocumentScopes.Write).WithName("RemoveGroupInbox");
        inbox.MapPost("/documents", UploadAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("UploadToGroupInbox")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit)
            .WithDescription("Uploads into the group's inbox. Only members may; the upload needs Contribute on the library as usual.");

        var me = endpoints.MapGroup("/v1.0/me").WithTags("Documents");
        me.MapGet("/inboxes", MyInboxesAsync).RequireScope(DocumentScopes.Read).WithName("ListMyInboxes")
            .WithDescription("The caller's inboxes: the personal one and those of their groups that they can read.");
        me.MapPost("/inbox/documents", UploadToInboxAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("UploadToInbox")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit)
            .WithDescription("Uploads into the caller's Inbox library (LST-07), created on first use.");
    }

    private static async Task<Results<Ok<GroupInboxResponse>, ProblemHttpResult>> GetAsync(
        [FromRoute(Name = "id")] Guid groupId, Caller caller, DocumentsDbContext db, IUserDirectory directory, IListItemStore items, CancellationToken ct)
    {
        var inbox = await DocumentQueries.InboxOfGroupAsync(db, caller.TenantId, groupId, ct);
        var list = inbox is null ? null : await items.GetListAsync(inbox.WorkspaceId, inbox.ListId, ct);
        if (inbox is null || list is null)
        {
            return ApiErrors.NotFound("The group has no inbox you can see.");
        }

        var names = await directory.GetGroupNamesAsync(caller.TenantId, [groupId], ct);
        return TypedResults.Ok(new GroupInboxResponse(groupId, names.GetValueOrDefault(groupId), inbox.WorkspaceId, inbox.ListId, list.Name));
    }

    private static async Task<Results<Ok<GroupInboxResponse>, ValidationProblem, ProblemHttpResult>> SetAsync(
        [FromRoute(Name = "id")] Guid groupId, GroupInboxRequest request, Caller caller, DocumentsDbContext db, IUserDirectory directory, IListItemStore items,
        CancellationToken ct)
    {
        if (request.WorkspaceId == Guid.Empty || request.ListId == Guid.Empty)
        {
            return ApiErrors.Validation("listId", "workspaceId and listId are required.");
        }

        if (!await directory.GroupExistsAsync(caller.TenantId, groupId, ct))
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
            return ApiErrors.BadRequest("notALibrary", "A group inbox must be a library.");
        }

        var existing = await DocumentQueries.InboxOfGroupAsync(db, caller.TenantId, groupId, ct);
        if (list.Access < WorkspaceAccessLevel.Manage || (existing is not null && !await CanManageAsync(items, caller, existing, ct)))
        {
            return DocumentService.Forbidden();
        }

        if (existing is null)
        {
            existing = new GroupInbox { Id = Ids.New(), TenantId = caller.TenantId, GroupId = groupId };
            db.GroupInboxes.Add(existing);
        }

        existing.WorkspaceId = request.WorkspaceId;
        existing.ListId = request.ListId;
        await db.SaveChangesAsync(ct);
        var names = await directory.GetGroupNamesAsync(caller.TenantId, [groupId], ct);
        return TypedResults.Ok(new GroupInboxResponse(groupId, names.GetValueOrDefault(groupId), existing.WorkspaceId, existing.ListId, list.Name));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        [FromRoute(Name = "id")] Guid groupId, Caller caller, DocumentsDbContext db, IListItemStore items, CancellationToken ct)
    {
        if (await DocumentQueries.InboxOfGroupAsync(db, caller.TenantId, groupId, ct) is not { } existing)
        {
            return ApiErrors.NotFound();
        }

        if (!await CanManageAsync(items, caller, existing, ct))
        {
            return DocumentService.Forbidden();
        }

        db.GroupInboxes.Remove(existing);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        [FromRoute(Name = "id")] Guid groupId, IFormFile? file, [FromForm] string? title, [FromForm] string? languages, Caller caller,
        IUserDirectory directory, DocumentsDbContext db, DocumentService documents, CancellationToken ct)
    {
        var inbox = await DocumentQueries.InboxOfGroupAsync(db, caller.TenantId, groupId, ct);
        if (inbox is null || !(await directory.GetGroupIdsAsync(caller.TenantId, caller.UserId, ct)).Contains(groupId))
        {
            return ApiErrors.NotFound("The group has no inbox, or you are not a member.");
        }

        return await documents.UploadAsync(caller.Actor, inbox.WorkspaceId, inbox.ListId, file, title, null, languages, null, ct);
    }

    private static async Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadToInboxAsync(
        IFormFile? file, [FromForm] string? title, [FromForm] string? languages, Caller caller, IListItemStore items, DocumentService documents,
        CancellationToken ct)
    {
        var home = await items.EnsureHomeAsync(ct);
        return await documents.UploadAsync(caller.Actor, home.WorkspaceId, home.InboxListId, file, title, null, languages, null, ct);
    }

    private static async Task<Ok<List<InboxResponse>>> MyInboxesAsync(
        Caller caller, IUserDirectory directory, DocumentsDbContext db, IListItemStore items, CancellationToken ct)
    {
        var home = await items.EnsureHomeAsync(ct);
        var result = new List<InboxResponse> { new("personal", home.WorkspaceId, home.InboxListId, "Inbox", null, null) };
        var inboxes = new List<GroupInbox>();
        foreach (var groupId in await directory.GetGroupIdsAsync(caller.TenantId, caller.UserId, ct))
        {
            if (await DocumentQueries.InboxOfGroupAsync(db, caller.TenantId, groupId, ct) is { } inbox)
            {
                inboxes.Add(inbox);
            }
        }

        var names = await directory.GetGroupNamesAsync(caller.TenantId, [.. inboxes.Select(g => g.GroupId)], ct);
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
    private static async Task<bool> CanManageAsync(IListItemStore items, Caller caller, GroupInbox inbox, CancellationToken ct) =>
        await items.GetListAsync(inbox.WorkspaceId, inbox.ListId, ct) is { } list
            ? list.Access >= WorkspaceAccessLevel.Manage
            : await items.AsSystem(caller.Actor).GetListAsync(inbox.WorkspaceId, inbox.ListId, ct) is null;
}

/// <summary>Removes the inbox of a deleted group (IAM-14), in the background (a Wolverine handler generated ahead of time).</summary>
public static class GroupInboxCleanupSubscriber
{
    public static async Task Handle(PrincipalDeleted e, DocumentsDbContext db, CancellationToken cancellationToken)
    {
        if (e.IsGroup && await DocumentQueries.InboxOfGroupAsync(db, e.TenantId, e.PrincipalId, cancellationToken) is { } inbox)
        {
            db.GroupInboxes.Remove(inbox);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
