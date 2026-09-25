using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Tasks.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Tasks.Features;

public sealed record ChecklistEntryDto(string Text, bool Done);

public sealed record ChecklistResponse([property: JsonPropertyName("value")] IReadOnlyList<ChecklistEntryDto> Value);

/// <summary>Another item, as shown in links: only items the caller can read are listed.</summary>
public sealed record LinkedItem(Guid LinkId, Guid WorkspaceId, Guid ListId, Guid ItemId, string? Title, string? Status);

public sealed record TaskLinksResponse(
    LinkedItem? Parent, IReadOnlyList<LinkedItem> Subtasks, IReadOnlyList<LinkedItem> BlockedBy, IReadOnlyList<LinkedItem> Blocking, IReadOnlyList<LinkedItem> Documents);

public sealed record AddLinkRequest(TaskLinkKind Kind, Guid WorkspaceId, Guid ListId, Guid ItemId);

public sealed record RecurrenceRequest(string Rule);

public sealed record RecurrenceResponse(string Rule, DateOnly? NextDueDate);

/// <summary>A task in a cross-list view (TSK-03).</summary>
public sealed record MyTask(
    Guid WorkspaceId, Guid ListId, string ListName, Guid ItemId, string? Title, string? Status, string? Priority, string? DueDate, IReadOnlyList<Guid> AssignedTo);

public sealed record MyTasksResponse([property: JsonPropertyName("value")] IReadOnlyList<MyTask> Value);

/// <summary>Creates a task about a document (TSK-06): in <c>workspaceId</c>/<c>listId</c> (a task list).</summary>
public sealed record TaskFromDocumentRequest(Guid WorkspaceId, Guid ListId, string? Title, DateOnly? DueDate, IReadOnlyList<Guid>? AssignedTo);

/// <summary>
/// Task features on list items: checklists (TSK-01), subtasks and dependencies (TSK-02), cross-list
/// views (TSK-03), recurrence (TSK-05) and tasks from documents (TSK-06). Access comes from the
/// lists engine (<see cref="IListItemStore"/>): reading needs Read on the item, changing Contribute.
/// </summary>
internal static class TaskEndpoints
{
    public const int MaxChecklistEntries = 200;
    private const int MaxMyTasks = 500;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var item = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}", "Tasks");
        item.MapGet("/checklist", GetChecklistAsync).RequireScope(TaskScopes.Read).WithName("GetChecklist");
        item.MapPut("/checklist", ReplaceChecklistAsync).RequireScope(TaskScopes.Write).WithName("ReplaceChecklist");
        item.MapGet("/links", GetLinksAsync).RequireScope(TaskScopes.Read).WithName("GetTaskLinks");
        item.MapPost("/links", AddLinkAsync).RequireScope(TaskScopes.Write).WithName("AddTaskLink");
        item.MapDelete("/links/{linkId:guid}", RemoveLinkAsync).RequireScope(TaskScopes.Write).WithName("RemoveTaskLink");
        item.MapGet("/recurrence", GetRecurrenceAsync).RequireScope(TaskScopes.Read).WithName("GetTaskRecurrence");
        item.MapPut("/recurrence", SetRecurrenceAsync).RequireScope(TaskScopes.Write).WithName("SetTaskRecurrence");
        item.MapDelete("/recurrence", RemoveRecurrenceAsync).RequireScope(TaskScopes.Write).WithName("RemoveTaskRecurrence");
        item.MapGet("/tasks", TasksOfDocumentAsync).RequireScope(TaskScopes.Read).WithName("ListTasksOfDocument");
        item.MapPost("/tasks", CreateTaskFromDocumentAsync).RequireScope(TaskScopes.Write).WithName("CreateTaskFromDocument");

        endpoints.MapV1Group("me", "Tasks").MapGet("/tasks", MyTasksAsync).RequireScope(TaskScopes.Read).WithName("ListMyTasks");
    }

    // ---- Checklist -----------------------------------------------------------

    private static async Task<Results<Ok<ChecklistResponse>, ProblemHttpResult>> GetChecklistAsync(
        Guid workspaceId, Guid listId, Guid itemId, TaskAccess access, TasksDbContext db, CancellationToken ct)
    {
        if (await access.TaskAsync(workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        return TypedResults.Ok(new ChecklistResponse(await ChecklistAsync(db, itemId, ct)));
    }

    /// <summary>Replaces the checklist (order as given).</summary>
    private static async Task<Results<Ok<ChecklistResponse>, ValidationProblem, ProblemHttpResult>> ReplaceChecklistAsync(
        Guid workspaceId, Guid listId, Guid itemId, List<ChecklistEntryDto> entries, TaskAccess access, TasksDbContext db, CancellationToken ct)
    {
        if (entries.Count > MaxChecklistEntries || entries.Any(e => string.IsNullOrWhiteSpace(e.Text) || e.Text.Length > 500))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["checklist"] = [$"Up to {MaxChecklistEntries} entries with 1 to 500 characters each."] });
        }

        var task = await access.TaskAsync(workspaceId, listId, itemId, ct);
        if (task is null)
        {
            return ApiErrors.NotFound();
        }

        if (task.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        await db.Checklist.Where(c => c.ItemId == itemId).ExecuteDeleteAsync(ct);
        db.Checklist.AddRange(entries.Select((e, i) => new ChecklistEntry { Id = Ids.New(), ItemId = itemId, Position = i, Text = e.Text.Trim(), Done = e.Done }));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ChecklistResponse(await ChecklistAsync(db, itemId, ct)));
    }

    internal static async Task<List<ChecklistEntryDto>> ChecklistAsync(TasksDbContext db, Guid itemId, CancellationToken ct) =>
        await db.Checklist.AsNoTracking().Where(c => c.ItemId == itemId).OrderBy(c => c.Position)
            .Select(c => new ChecklistEntryDto(c.Text, c.Done)).ToListAsync(ct);

    // ---- Links ---------------------------------------------------------------

    private static async Task<Results<Ok<TaskLinksResponse>, ProblemHttpResult>> GetLinksAsync(
        Guid workspaceId, Guid listId, Guid itemId, TaskAccess access, TasksDbContext db, CancellationToken ct)
    {
        if (await access.TaskAsync(workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var outgoing = await db.Links.AsNoTracking().Where(l => l.ItemId == itemId).OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var incoming = await db.Links.AsNoTracking().Where(l => l.TargetItemId == itemId && l.Kind != TaskLinkKind.Document).OrderBy(l => l.CreatedAt).ToListAsync(ct);

        async Task<List<LinkedItem>> TargetsAsync(TaskLinkKind kind) =>
            await access.VisibleAsync(outgoing.Where(l => l.Kind == kind).Select(l => (l.Id, l.TargetWorkspaceId, l.TargetListId, l.TargetItemId)), ct);

        async Task<List<LinkedItem>> SourcesAsync(TaskLinkKind kind) =>
            await access.VisibleAsync(incoming.Where(l => l.Kind == kind).Select(l => (l.Id, l.WorkspaceId, l.ListId, l.ItemId)), ct);

        return TypedResults.Ok(new TaskLinksResponse(
            (await SourcesAsync(TaskLinkKind.Subtask)).FirstOrDefault(),
            await TargetsAsync(TaskLinkKind.Subtask),
            await TargetsAsync(TaskLinkKind.BlockedBy),
            await SourcesAsync(TaskLinkKind.BlockedBy),
            await TargetsAsync(TaskLinkKind.Document)));
    }

    /// <summary>
    /// Links the task to another item: <c>subtask</c> (the target becomes a subtask; one parent per
    /// task), <c>blockedBy</c> (the task waits for the target) or <c>document</c>. Cycles are rejected.
    /// </summary>
    private static async Task<Results<Created<LinkedItem>, ValidationProblem, ProblemHttpResult>> AddLinkAsync(
        Guid workspaceId, Guid listId, Guid itemId, AddLinkRequest request, TaskAccess access, TasksDbContext db, CancellationToken ct)
    {
        var task = await access.TaskAsync(workspaceId, listId, itemId, ct);
        if (task is null)
        {
            return ApiErrors.NotFound();
        }

        if (task.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        var target = request.Kind == TaskLinkKind.Document
            ? await access.DocumentAsync(request.WorkspaceId, request.ListId, request.ItemId, ct)
            : await access.TaskAsync(request.WorkspaceId, request.ListId, request.ItemId, ct);
        if (target is null || request.ItemId == itemId)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]>
            {
                ["itemId"] = [request.Kind == TaskLinkKind.Document ? "A document you can read is expected." : "Another task you can read is expected."],
            });
        }

        if (await db.Links.AnyAsync(l => l.ItemId == itemId && l.TargetItemId == request.ItemId && l.Kind == request.Kind, ct))
        {
            return ApiErrors.Conflict("linkExists", "The items are already linked this way.");
        }

        if (request.Kind == TaskLinkKind.Subtask && await db.Links.AnyAsync(l => l.TargetItemId == request.ItemId && l.Kind == TaskLinkKind.Subtask, ct))
        {
            return ApiErrors.Conflict("hasParent", "The task already is a subtask of another task.");
        }

        if (request.Kind != TaskLinkKind.Document && await ReachesAsync(db, request.ItemId, itemId, request.Kind, ct))
        {
            return ApiErrors.Conflict("cycle", "The link would create a cycle.");
        }

        var link = new TaskLink
        {
            Id = Ids.New(),
            Kind = request.Kind,
            ItemId = itemId,
            WorkspaceId = workspaceId,
            ListId = listId,
            TargetItemId = target.Id,
            TargetWorkspaceId = target.WorkspaceId,
            TargetListId = target.ListId,
        };
        db.Links.Add(link);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created(
            $"{ApiRoutes.V1}/workspaces/{workspaceId}/lists/{listId}/items/{itemId}/links",
            new LinkedItem(link.Id, target.WorkspaceId, target.ListId, target.Id, target.Fields["title"]?.GetValue<string>(), Status(target)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveLinkAsync(
        Guid workspaceId, Guid listId, Guid itemId, Guid linkId, TaskAccess access, TasksDbContext db, CancellationToken ct)
    {
        var task = await access.TaskAsync(workspaceId, listId, itemId, ct);
        var link = task is null ? null : await db.Links.FirstOrDefaultAsync(l => l.Id == linkId && (l.ItemId == itemId || l.TargetItemId == itemId), ct);
        if (link is null)
        {
            return ApiErrors.NotFound();
        }

        if (task!.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        db.Links.Remove(link);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Whether <paramref name="to"/> can be reached from <paramref name="from"/> over links of <paramref name="kind"/>.</summary>
    private static async Task<bool> ReachesAsync(TasksDbContext db, Guid from, Guid to, TaskLinkKind kind, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { from };
        var frontier = new List<Guid> { from };
        while (frontier.Count > 0)
        {
            if (frontier.Contains(to))
            {
                return true;
            }

            var current = frontier;
            frontier = (await db.Links.AsNoTracking().Where(l => l.Kind == kind && current.Contains(l.ItemId)).Select(l => l.TargetItemId).ToListAsync(ct))
                .Where(seen.Add)
                .ToList();
        }

        return false;
    }

    // ---- Recurrence ------------------------------------------------------------

    private static async Task<Results<Ok<RecurrenceResponse>, ProblemHttpResult>> GetRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, TaskAccess access, TasksDbContext db, CancellationToken ct)
    {
        var task = await access.TaskAsync(workspaceId, listId, itemId, ct);
        var recurrence = task is null ? null : await db.Recurrences.AsNoTracking().FirstOrDefaultAsync(r => r.ItemId == itemId, ct);
        return recurrence is null
            ? ApiErrors.NotFound("The task does not repeat.")
            : TypedResults.Ok(new RecurrenceResponse(recurrence.Rule, Recurrences.Next(recurrence.Rule, DueDate(task!))));
    }

    /// <summary>Makes the task repeat (RFC 5545 RRULE, e.g. <c>FREQ=MONTHLY;BYMONTHDAY=1</c>); the task needs a due date.</summary>
    private static async Task<Results<Ok<RecurrenceResponse>, ValidationProblem, ProblemHttpResult>> SetRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, RecurrenceRequest request, TaskAccess access, TasksDbContext db, CancellationToken ct)
    {
        var rule = request.Rule?.Trim() ?? string.Empty;
        if (rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            rule = rule[6..];
        }

        if (rule.Length is 0 or > 500 || !Recurrences.IsValid(rule))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["rule"] = ["A valid RRULE is expected, e.g. FREQ=WEEKLY;BYDAY=MO."] });
        }

        var task = await access.TaskAsync(workspaceId, listId, itemId, ct);
        if (task is null)
        {
            return ApiErrors.NotFound();
        }

        if (task.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        if (DueDate(task) is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["dueDate"] = ["A repeating task needs a due date."] });
        }

        var recurrence = await db.Recurrences.FirstOrDefaultAsync(r => r.ItemId == itemId, ct);
        if (recurrence is null)
        {
            recurrence = new TaskRecurrence { Id = Ids.New(), ItemId = itemId, WorkspaceId = workspaceId, ListId = listId, Rule = rule };
            db.Recurrences.Add(recurrence);
        }

        recurrence.Rule = rule;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new RecurrenceResponse(rule, Recurrences.Next(rule, DueDate(task))));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, TaskAccess access, TasksDbContext db, CancellationToken ct)
    {
        var task = await access.TaskAsync(workspaceId, listId, itemId, ct);
        if (task is null)
        {
            return ApiErrors.NotFound();
        }

        if (task.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        await db.Recurrences.Where(r => r.ItemId == itemId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    // ---- Tasks from documents ----------------------------------------------------

    /// <summary>Tasks linked to a document (TSK-06), those the caller can read.</summary>
    private static async Task<Results<Ok<MyTasksResponse>, ProblemHttpResult>> TasksOfDocumentAsync(
        Guid workspaceId, Guid listId, Guid itemId, TaskAccess access, TasksDbContext db, IListItemStore items, CancellationToken ct)
    {
        if (await access.DocumentAsync(workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var links = await db.Links.AsNoTracking().Where(l => l.TargetItemId == itemId && l.Kind == TaskLinkKind.Document).ToListAsync(ct);
        var result = new List<MyTask>();
        foreach (var link in links)
        {
            if (await items.GetAsync(link.WorkspaceId, link.ListId, link.ItemId, ct) is { } task
                && await items.GetListAsync(link.WorkspaceId, link.ListId, ct) is { } list)
            {
                result.Add(ToMyTask(list, task));
            }
        }

        return TypedResults.Ok(new MyTasksResponse(result));
    }

    /// <summary>Creates a task in a task list, linked to the document (e.g. "pay this invoice").</summary>
    private static async Task<Results<Created<MyTask>, ValidationProblem, ProblemHttpResult>> CreateTaskFromDocumentAsync(
        Guid workspaceId, Guid listId, Guid itemId, TaskFromDocumentRequest request, TaskAccess access, TasksDbContext db,
        IListItemStore items, CancellationToken ct)
    {
        var document = await access.DocumentAsync(workspaceId, listId, itemId, ct);
        if (document is null)
        {
            return ApiErrors.NotFound();
        }

        var taskList = await items.GetListAsync(request.WorkspaceId, request.ListId, ct);
        if (taskList is null || !taskList.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["listId"] = ["A task list you can see is expected."] });
        }

        var fields = new JsonObject
        {
            ["title"] = string.IsNullOrWhiteSpace(request.Title) ? document.Fields["title"]?.GetValue<string>() ?? "Task" : request.Title,
        };
        if (request.DueDate is { } due)
        {
            fields["dueDate"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (request.AssignedTo is { Count: > 0 } assignees)
        {
            fields["assignedTo"] = new JsonArray([.. assignees.Select(a => (JsonNode)JsonValue.Create(a.ToString())!)]);
        }

        // The list's default content type: task lists created from the template have only "task".
        var created = await items.CreateAsync(taskList.WorkspaceId, taskList.Id, fields, null, ct);
        switch (created.Status)
        {
            case ListItemStatus.Ok:
                break;
            case ListItemStatus.Forbidden:
                return TaskAccess.Forbidden();
            case ListItemStatus.Invalid:
                return ApiErrors.Validation(created.Errors!.ToDictionary());
            default:
                return ApiErrors.Conflict("rejected", created.Message ?? "The task was not created.");
        }

        var task = created.Item!;
        db.Links.Add(new TaskLink
        {
            Id = Ids.New(),
            Kind = TaskLinkKind.Document,
            ItemId = task.Id,
            WorkspaceId = task.WorkspaceId,
            ListId = task.ListId,
            TargetItemId = document.Id,
            TargetWorkspaceId = document.WorkspaceId,
            TargetListId = document.ListId,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{task.WorkspaceId}/lists/{task.ListId}/items/{task.Id}", ToMyTask(taskList, task));
    }

    // ---- My tasks ---------------------------------------------------------------

    /// <summary>
    /// Open tasks across every task list the caller can read (TSK-03): <c>view=mine</c> (assigned to
    /// me, default), <c>dueThisWeek</c> (mine, due today to Sunday), <c>overdue</c> (mine, due before
    /// today) or <c>all</c> (every open task). Dates are UTC; ordered by due date.
    /// </summary>
    private static async Task<Results<Ok<MyTasksResponse>, ValidationProblem>> MyTasksAsync(
        string? view, int? top, IListItemStore items, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var endOfWeek = today.AddDays(((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7);
        var mine = $"fields/assignedTo/any(a: a eq {user.UserId})";
        var open = $"fields/status ne '{TaskTemplates.Completed}'";
        var filter = (view ?? "mine") switch
        {
            "mine" => $"{open} and {mine}",
            "dueThisWeek" => $"{open} and {mine} and fields/dueDate ge {Date(today)} and fields/dueDate le {Date(endOfWeek)}",
            "overdue" => $"{open} and {mine} and fields/dueDate lt {Date(today)}",
            "all" => open,
            _ => null,
        };
        if (filter is null || user.UserId is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["view"] = ["Use mine, dueThisWeek, overdue or all."] });
        }

        var limit = Math.Clamp(top ?? 100, 1, MaxMyTasks);
        var lists = (await items.GetListsAsync(null, null, ct)).Where(l => l.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey)).ToList();
        var (pages, error) = await items.QueryAsync(lists, new ListItemQuery(filter, "fields/dueDate", MaxMyTasks), ct);
        if (error is not null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["filter"] = [error] });
        }

        var result = pages.SelectMany(page => page.Items.Select(task => ToMyTask(page.List, task))).ToList();
        return TypedResults.Ok(new MyTasksResponse(result
            .OrderBy(t => t.DueDate ?? "9999-12-31", StringComparer.Ordinal)
            .ThenBy(t => t.Title, StringComparer.CurrentCulture)
            .Take(limit)
            .ToList()));
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static DateOnly? DueDate(ListItemData task) =>
        task.Fields["dueDate"] is JsonValue due && DateOnly.TryParse(due.GetValue<string>(), CultureInfo.InvariantCulture, out var date) ? date : null;

    private static string? Status(ListItemData item) => item.Fields["status"] is JsonValue status ? status.GetValue<string>() : null;

    internal static MyTask ToMyTask(ListData list, ListItemData task) => new(
        task.WorkspaceId,
        task.ListId,
        list.Name,
        task.Id,
        task.Fields["title"]?.GetValue<string>(),
        Status(task),
        task.Fields["priority"] is JsonValue priority ? priority.GetValue<string>() : null,
        task.Fields["dueDate"] is JsonValue due ? due.GetValue<string>() : null,
        task.Fields["assignedTo"] is JsonArray assigned ? [.. assigned.Select(a => Guid.Parse(a!.GetValue<string>()))] : []);
}

/// <summary>Resolves tasks and documents with the caller's access, via the lists engine.</summary>
internal sealed class TaskAccess(IListItemStore items)
{
    public static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    /// <summary>The item when it is readable and its list has the task content type.</summary>
    public async Task<ListItemData?> TaskAsync(Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        return list is null || !list.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey) ? null : await items.GetAsync(workspaceId, listId, itemId, ct);
    }

    /// <summary>The item when it is readable and in a library.</summary>
    public async Task<ListItemData?> DocumentAsync(Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        return list is not { IsLibrary: true } ? null : await items.GetAsync(workspaceId, listId, itemId, ct);
    }

    /// <summary>The linked items the caller can read.</summary>
    public async Task<List<LinkedItem>> VisibleAsync(IEnumerable<(Guid LinkId, Guid WorkspaceId, Guid ListId, Guid ItemId)> links, CancellationToken ct)
    {
        var result = new List<LinkedItem>();
        foreach (var (linkId, workspaceId, listId, itemId) in links)
        {
            if (await items.GetAsync(workspaceId, listId, itemId, ct) is { } item)
            {
                result.Add(new LinkedItem(linkId, workspaceId, listId, itemId, item.Fields["title"]?.GetValue<string>(),
                    item.Fields["status"] is JsonValue status ? status.GetValue<string>() : null));
            }
        }

        return result;
    }
}
