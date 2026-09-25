using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Collaboration.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Collaboration.Features;

/// <summary>Create body: <c>{ "text", "parentId"?, "mentions"?: [userId] }</c>. Mentioned users are notified.</summary>
public sealed record CommentRequest(string? Text, Guid? ParentId, IReadOnlyList<Guid>? Mentions);

/// <summary>Update body: <c>{ "text", "mentions"? }</c> (the reply target cannot change).</summary>
public sealed record CommentUpdateRequest(string? Text, IReadOnlyList<Guid>? Mentions);

public sealed record CommentResponse(
    Guid Id, Guid ItemId, Guid? ParentId, string Text, IReadOnlyList<Guid> Mentions,
    DateTimeOffset CreatedAt, Guid? CreatedBy, DateTimeOffset UpdatedAt, Guid? UpdatedBy)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }


    internal static CommentResponse From(Comment c) =>
        new(c.Id, c.ItemId, c.ParentId, c.Text, c.Mentions, c.CreatedAt, c.CreatedBy, c.UpdatedAt, c.UpdatedBy) { ETag = ETags.From(c.Version) };
}

public sealed record ActivityResponse(Guid Id, string Kind, Guid? ActorId, string? Summary, IReadOnlyList<string> ChangedFields, DateTimeOffset At);

/// <summary>
/// Comments and the activity timeline of list items (LST-17). Access comes from the lists engine:
/// reading needs Read on the item, commenting Contribute; authors change their comments, and
/// people with Manage on the item may delete any comment.
/// </summary>
internal static class CommentEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var item = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}", "Comments");
        item.MapGet("/comments", ListAsync).RequireScope(CollaborationScopes.Read).WithName("ListComments");
        item.MapPost("/comments", CreateAsync).RequireScope(CollaborationScopes.Write).WithName("CreateComment");
        item.MapGet("/comments/{commentId:guid}", GetAsync).RequireScope(CollaborationScopes.Read).WithName("GetComment");
        item.MapPatch("/comments/{commentId:guid}", UpdateAsync).RequireScope(CollaborationScopes.Write).WithName("UpdateComment");
        item.MapDelete("/comments/{commentId:guid}", DeleteAsync).RequireScope(CollaborationScopes.Write).WithName("DeleteComment");
        endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}", "Activity")
            .MapGet("/activity", ActivityAsync).RequireScope(CollaborationScopes.Read).WithName("ListItemActivity");
    }

    /// <summary>Comments of the item, oldest first (replies carry <c>parentId</c>).</summary>
    private static async Task<Results<Ok<Page<CommentResponse>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, CollaborationDbContext db, HttpRequest http, CancellationToken ct)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.From(http);
        var query = db.Comments.AsNoTracking().Where(c => c.ItemId == itemId && c.ListId == listId);
        if (page.After is { } after)
        {
            query = query.Where(c => c.Id.CompareTo(after) > 0);
        }

        var comments = await query.OrderBy(c => c.Id).Take(page.Top + 1).ToListAsync(ct);
        return TypedResults.Ok(Page.Create(comments.Select(CommentResponse.From).ToList(), page, http, c => c.Id));
    }

    private static async Task<Results<Ok<CommentResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid listId, Guid itemId, Guid commentId, IListItemStore items, CollaborationDbContext db, HttpResponse response, CancellationToken ct)
    {
        var comment = await items.GetAsync(workspaceId, listId, itemId, ct) is null
            ? null
            : await db.Comments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commentId && c.ItemId == itemId && c.ListId == listId, ct);
        if (comment is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, comment.Version);
        return TypedResults.Ok(CommentResponse.From(comment));
    }

    private static async Task<Results<Created<CommentResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, Guid listId, Guid itemId, CommentRequest request, IListItemStore items, CollaborationDbContext db,
        CommentMentions mentions, ICurrentUser user, TimeProvider time, HttpResponse response, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (item.Access < WorkspaceAccessLevel.Contribute)
        {
            return Forbidden();
        }

        var errors = await mentions.ValidateAsync(request.Text, request.Mentions, ct);
        if (request.ParentId is { } parentId
            && !await db.Comments.AnyAsync(c => c.Id == parentId && c.ItemId == itemId && c.ParentId == null, ct))
        {
            errors["parentId"] = ["Replies must point to a top-level comment of the same item."];
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var comment = new Comment
        {
            Id = Ids.New(),
            WorkspaceId = workspaceId,
            ListId = listId,
            ItemId = itemId,
            ParentId = request.ParentId,
            Text = request.Text!.Trim(),
            Mentions = [.. (request.Mentions ?? []).Distinct()],
        };
        db.Comments.Add(comment);
        db.Activity.Add(ItemActivity.Create(workspaceId, listId, itemId, ActivityKinds.Commented, user.UserId, Excerpt(comment.Text), [], $"comment:{comment.Id:N}", time.GetUtcNow()));
        await db.SaveChangesAsync(ct);

        await mentions.NotifyAsync(item, comment, comment.Mentions, ct);
        await items.ReindexAsync(itemId, ct);
        ETags.Set(response, comment.Version);
        return TypedResults.Created($"/v1.0/workspaces/{workspaceId}/lists/{listId}/items/{itemId}/comments/{comment.Id}", CommentResponse.From(comment));
    }

    /// <summary>Changes the text (authors only; needs <c>If-Match</c>). Users newly mentioned are notified.</summary>
    private static async Task<Results<Ok<CommentResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, Guid listId, Guid itemId, Guid commentId, CommentUpdateRequest request, IListItemStore items, CollaborationDbContext db,
        CommentMentions mentions, ICurrentUser user, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var comment = item is null ? null : await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.ItemId == itemId && c.ListId == listId, ct);
        if (comment is null)
        {
            return ApiErrors.NotFound();
        }

        if (comment.CreatedBy != user.UserId || item!.Access < WorkspaceAccessLevel.Contribute)
        {
            return Forbidden();
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (version != comment.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        var errors = await mentions.ValidateAsync(request.Text, request.Mentions, ct);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var added = (request.Mentions ?? []).Distinct().Except(comment.Mentions).ToList();
        comment.Text = request.Text!.Trim();
        comment.Mentions = [.. (request.Mentions ?? []).Distinct()];
        await db.SaveChangesAsync(ct);

        await mentions.NotifyAsync(item, comment, added, ct);
        await items.ReindexAsync(itemId, ct);
        ETags.Set(response, comment.Version);
        return TypedResults.Ok(CommentResponse.From(comment));
    }

    /// <summary>Deletes the comment and its replies (the author, or someone with Manage on the item).</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid listId, Guid itemId, Guid commentId, IListItemStore items, CollaborationDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var comment = item is null ? null : await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.ItemId == itemId && c.ListId == listId, ct);
        if (comment is null)
        {
            return ApiErrors.NotFound();
        }

        if (!(comment.CreatedBy == user.UserId && item!.Access >= WorkspaceAccessLevel.Contribute) && item!.Access < WorkspaceAccessLevel.Manage)
        {
            return Forbidden();
        }

        db.Comments.RemoveRange(await db.Comments.Where(c => c.ParentId == commentId).ToListAsync(ct));
        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);
        await items.ReindexAsync(itemId, ct);
        return TypedResults.NoContent();
    }

    /// <summary>The item's timeline, newest first: changes, comments and entries of modules and extensions.</summary>
    private static async Task<Results<Ok<Page<ActivityResponse>>, ProblemHttpResult>> ActivityAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, CollaborationDbContext db, HttpRequest http, CancellationToken ct)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.From(http);
        var query = db.Activity.AsNoTracking().Where(a => a.ItemId == itemId && a.ListId == listId);
        // Entries are recorded asynchronously, so order by the time of the change (the id breaks ties).
        if (page.After is { } after
            && await db.Activity.AsNoTracking().Where(a => a.Id == after && a.ItemId == itemId).Select(a => (DateTimeOffset?)a.At).FirstOrDefaultAsync(ct) is { } at)
        {
            query = query.Where(a => a.At < at || (a.At == at && a.Id.CompareTo(after) < 0));
        }

        var entries = await query.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(page.Top + 1).ToListAsync(ct);
        return TypedResults.Ok(Page.Create(
            entries.Select(a => new ActivityResponse(a.Id, a.Kind, a.ActorId, a.Summary, a.ChangedFields, a.At)).ToList(), page, http, a => a.Id));
    }

    private static string Excerpt(string text) => text.Length <= 200 ? text : text[..199] + "…";

    private static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action on the item.");
}

/// <summary>Validates comment text and mentions and notifies mentioned users who can read the item.</summary>
internal sealed class CommentMentions(IUserDirectory users, INotificationSender sender, ITenantContext tenant, ICurrentUser user, ITenantScopeFactory scopes)
{
    public async Task<Dictionary<string, string[]>> ValidateAsync(string? text, IReadOnlyList<Guid>? mentions, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > CommentRules.MaxLength)
        {
            errors["text"] = [$"Enter 1 to {CommentRules.MaxLength} characters."];
        }

        var ids = (mentions ?? []).Distinct().ToList();
        if (ids.Count > CommentRules.MaxMentions)
        {
            errors["mentions"] = [$"Mention up to {CommentRules.MaxMentions} people."];
        }
        else if (ids.Count > 0 && (await users.GetUserNamesAsync(ids, ct)).Count != ids.Count)
        {
            errors["mentions"] = ["Mentions must be users of the organization."];
        }

        return errors;
    }

    /// <summary>Notifies <paramref name="mentioned"/> (except the author) who can read the item.</summary>
    public async Task NotifyAsync(ListItemData item, Comment comment, IReadOnlyCollection<Guid> mentioned, CancellationToken ct)
    {
        var recipients = new List<Guid>();
        foreach (var userId in mentioned.Where(u => u != user.UserId))
        {
            await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!, userId);
            if (await scope.ServiceProvider.GetRequiredService<IListItemStore>().GetAsync(item.WorkspaceId, item.ListId, item.Id, ct) is not null)
            {
                recipients.Add(userId);
            }
        }

        if (recipients.Count == 0)
        {
            return;
        }

        var title = item.Fields["title"]?.GetValue<string>() ?? "an item";
        await sender.SendAsync(
            new NotificationMessage(NotificationTypes.Mention, $"You were mentioned in a comment on {title}", comment.Text.Length <= 300 ? comment.Text : comment.Text[..299] + "…",
                new NotificationLink(item.WorkspaceId, item.ListId, item.Id), $"mention:{comment.Id:N}:{comment.Version}"),
            recipients, ct);
    }
}
