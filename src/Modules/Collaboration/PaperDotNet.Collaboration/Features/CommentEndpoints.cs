using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }

    internal static CommentResponse From(Comment c) =>
        new(c.Id, c.ItemId, c.ParentId, c.Text, CommentEndpoints.MentionsOf(c), c.CreatedAt, c.CreatedBy, c.UpdatedAt, c.UpdatedBy) { ETag = ETags.From(c.Version) };
}

public sealed record ActivityResponse(Guid Id, string Kind, Guid? ActorId, string? Summary, IReadOnlyList<string> ChangedFields, DateTimeOffset At)
{
    internal static ActivityResponse From(ActivityEntry a) =>
        new(a.Id, a.Kind, a.ActorId, a.Summary, JsonSerializer.Deserialize(a.ChangedFields, CollaborationJson.Default.ListString) ?? [], a.At);
}

/// <summary>
/// Comments and the activity timeline of list items (LST-17). Access comes from the lists engine: reading needs Read on
/// the item, commenting Contribute; authors change their comments, and people with Manage on the item may delete any
/// comment.
/// </summary>
internal static class CommentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var item = app.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}");
        var comments = item.MapGroup("/comments").WithTags("Comments");
        comments.MapGet("", ListAsync).RequireScope(CollaborationScopes.Read).WithName("ListComments")
            .WithDescription("Oldest first; replies carry parentId.");
        comments.MapPost("", CreateAsync).RequireScope(CollaborationScopes.Write).WithName("CreateComment");
        comments.MapGet("/{commentId:guid}", GetAsync).RequireScope(CollaborationScopes.Read).WithName("GetComment");
        comments.MapPatch("/{commentId:guid}", UpdateAsync).RequireScope(CollaborationScopes.Write).WithName("UpdateComment")
            .WithDescription("Authors only; needs If-Match. Users newly mentioned are notified.");
        comments.MapDelete("/{commentId:guid}", DeleteAsync).RequireScope(CollaborationScopes.Write).WithName("DeleteComment")
            .WithDescription("Deletes the comment and its replies (the author, or someone with Manage on the item).");
        item.MapGet("/activity", ActivityAsync).RequireScope(CollaborationScopes.Read).WithTags("Activity").WithName("ListItemActivity")
            .WithDescription("The item's timeline, newest first: changes, comments and entries of modules and extensions.");
    }

    internal static List<Guid> MentionsOf(Comment comment) => JsonSerializer.Deserialize(comment.Mentions, CollaborationJson.Default.ListGuid) ?? [];

    private static async Task<Results<Ok<Page<CommentResponse>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, Guid listId, Guid itemId, HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, IListItemStore items, CollaborationDbContext database, CancellationToken cancellationToken)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var item = itemId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var comments = page.After is { } after
            ? await db.Comments.AsNoTracking().Where(c => c.TenantId == tenant && c.ItemId == item && c.Id.CompareTo(after) > 0).OrderBy(c => c.Id).Take(take).ToListAsync(ct)
            : await db.Comments.AsNoTracking().Where(c => c.TenantId == tenant && c.ItemId == item).OrderBy(c => c.Id).Take(take).ToListAsync(ct);
        return TypedResults.Ok(Page.Create([.. comments.Select(CommentResponse.From)], page, request, c => c.Id));
    }

    private static Task<Comment?> FindAsync(CollaborationDbContext database, Guid tenantId, Guid itemId, Guid commentId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var item = itemId;
        var id = commentId;
        var ct = cancellationToken;
        return db.Comments.FirstOrDefaultAsync(c => c.TenantId == tenant && c.Id == id && c.ItemId == item, ct);
    }

    private static async Task<Results<Ok<CommentResponse>, ProblemHttpResult>> GetAsync(
        Guid workspaceId, Guid listId, Guid itemId, Guid commentId, Caller caller, IListItemStore items, CollaborationDbContext db, HttpResponse response, CancellationToken ct)
    {
        var comment = await items.GetAsync(workspaceId, listId, itemId, ct) is null ? null : await FindAsync(db, caller.TenantId, itemId, commentId, ct);
        if (comment is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, comment.Version);
        return TypedResults.Ok(CommentResponse.From(comment));
    }

    private static async Task<Results<Created<CommentResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, Guid listId, Guid itemId, CommentRequest request, Caller caller, IListItemStore items, CollaborationDbContext database,
        IUserDirectory users, INotificationSender sender, TimeProvider time, HttpResponse response, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, cancellationToken);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (item.Access < WorkspaceAccessLevel.Contribute)
        {
            return Forbidden();
        }

        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var errors = await ValidateAsync(users, tenant, request.Text, request.Mentions, ct);
        if (request.ParentId is { } parentId)
        {
            var target = itemId;
            if (!await db.Comments.AnyAsync(c => c.TenantId == tenant && c.Id == parentId && c.ItemId == target && c.ParentId == null, ct))
            {
                errors["parentId"] = ["Replies must point to a top-level comment of the same item."];
            }
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var mentions = (request.Mentions ?? []).Distinct().ToList();
        var comment = new Comment
        {
            Id = Ids.New(),
            TenantId = tenant,
            WorkspaceId = workspaceId,
            ListId = listId,
            ItemId = itemId,
            ParentId = request.ParentId,
            Text = request.Text!.Trim(),
            Mentions = JsonSerializer.Serialize(mentions, CollaborationJson.Default.ListGuid),
        };
        db.Comments.Add(comment);
        db.Activity.Add(ItemActivity.Create(tenant, workspaceId, listId, itemId, ActivityKinds.Commented, caller.UserId, Excerpt(comment.Text), [], $"comment:{comment.Id:N}", time.GetUtcNow()));
        await db.SaveChangesAsync(ct);

        await NotifyAsync(items, sender, caller, item, comment, mentions, ct);
        ETags.Set(response, comment.Version);
        return TypedResults.Created($"/v1.0/workspaces/{workspaceId}/lists/{listId}/items/{itemId}/comments/{comment.Id}", CommentResponse.From(comment));
    }

    private static async Task<Results<Ok<CommentResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, Guid listId, Guid itemId, Guid commentId, CommentUpdateRequest request, Caller caller, IListItemStore items,
        CollaborationDbContext db, IUserDirectory users, INotificationSender sender, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var comment = item is null ? null : await FindAsync(db, caller.TenantId, itemId, commentId, ct);
        if (item is null || comment is null)
        {
            return ApiErrors.NotFound();
        }

        if (comment.CreatedBy != caller.UserId || item.Access < WorkspaceAccessLevel.Contribute)
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

        var errors = await ValidateAsync(users, caller.TenantId, request.Text, request.Mentions, ct);
        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var mentions = (request.Mentions ?? []).Distinct().ToList();
        var added = mentions.Except(MentionsOf(comment)).ToList();
        comment.Text = request.Text!.Trim();
        comment.Mentions = JsonSerializer.Serialize(mentions, CollaborationJson.Default.ListGuid);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        await NotifyAsync(items, sender, caller, item, comment, added, ct);
        ETags.Set(response, comment.Version);
        return TypedResults.Ok(CommentResponse.From(comment));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid workspaceId, Guid listId, Guid itemId, Guid commentId, Caller caller, IListItemStore items, CollaborationDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var item = await items.GetAsync(workspaceId, listId, itemId, cancellationToken);
        var comment = item is null ? null : await FindAsync(db, caller.TenantId, itemId, commentId, cancellationToken);
        if (item is null || comment is null)
        {
            return ApiErrors.NotFound();
        }

        if (!(comment.CreatedBy == caller.UserId && item.Access >= WorkspaceAccessLevel.Contribute) && item.Access < WorkspaceAccessLevel.Manage)
        {
            return Forbidden();
        }

        var tenant = caller.TenantId;
        var parent = comment.Id;
        var ct = cancellationToken;
        db.Comments.RemoveRange(await db.Comments.Where(c => c.TenantId == tenant && c.ParentId == parent).ToListAsync(ct));
        db.Comments.Remove(comment);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<Page<ActivityResponse>>, ProblemHttpResult>> ActivityAsync(
        Guid workspaceId, Guid listId, Guid itemId, HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken,
        Caller caller, IListItemStore items, CollaborationDbContext database, CancellationToken cancellationToken)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var item = itemId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        // Entries are recorded asynchronously, so the order is the time of the change (the id breaks ties).
        long? at = null;
        if (page.After is { } after)
        {
            at = await db.Activity.AsNoTracking().Where(a => a.TenantId == tenant && a.Id == after && a.ItemId == item).Select(a => (long?)a.AtUnixMs).FirstOrDefaultAsync(ct);
        }

        var entries = page.After is { } before && at is { } since
            ? await db.Activity.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.ItemId == item && (a.AtUnixMs < since || (a.AtUnixMs == since && a.Id.CompareTo(before) < 0)))
                .OrderByDescending(a => a.AtUnixMs).ThenByDescending(a => a.Id).Take(take).ToListAsync(ct)
            : await db.Activity.AsNoTracking().Where(a => a.TenantId == tenant && a.ItemId == item)
                .OrderByDescending(a => a.AtUnixMs).ThenByDescending(a => a.Id).Take(take).ToListAsync(ct);
        return TypedResults.Ok(Page.Create([.. entries.Select(ActivityResponse.From)], page, request, a => a.Id));
    }

    /// <summary>Checks the text and that mentions name users of the organization.</summary>
    private static async Task<Dictionary<string, string[]>> ValidateAsync(IUserDirectory users, Guid tenantId, string? text, IReadOnlyList<Guid>? mentions, CancellationToken ct)
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
        else if (ids.Count > 0 && (await users.GetUserNamesAsync(tenantId, ids, ct)).Count != ids.Count)
        {
            errors["mentions"] = ["Mentions must be users of the organization."];
        }

        return errors;
    }

    /// <summary>Notifies <paramref name="mentioned"/> (except the author) who can read the item.</summary>
    private static async Task NotifyAsync(
        IListItemStore items, INotificationSender sender, Caller caller, ListItemData item, Comment comment, IReadOnlyCollection<Guid> mentioned, CancellationToken ct)
    {
        var recipients = new List<Guid>();
        foreach (var userId in mentioned.Where(u => u != caller.UserId))
        {
            if (await items.ActingAs(new ChangeActor(caller.TenantId, userId)).GetAsync(item.WorkspaceId, item.ListId, item.Id, ct) is not null)
            {
                recipients.Add(userId);
            }
        }

        if (recipients.Count == 0)
        {
            return;
        }

        var title = item.Fields["title"]?.GetValue<string>() ?? "an item";
        await sender.SendAsync(caller.TenantId,
            new NotificationMessage(NotificationTypes.Mention, $"You were mentioned in a comment on {title}", comment.Text.Length <= 300 ? comment.Text : comment.Text[..299] + "…",
                new NotificationLink(item.WorkspaceId, item.ListId, item.Id), $"mention:{comment.Id:N}:{comment.Version}"),
            recipients, ct);
    }

    private static string Excerpt(string text) => text.Length <= 200 ? text : text[..199] + "…";

    private static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action on the item.");
}
