using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LinkedItem? Parent,
    IReadOnlyList<LinkedItem> Subtasks, IReadOnlyList<LinkedItem> BlockedBy, IReadOnlyList<LinkedItem> Blocking, IReadOnlyList<LinkedItem> Documents);

/// <summary>A link to add: <c>kind</c> is <c>subtask</c>, <c>blockedBy</c> or <c>document</c>.</summary>
public sealed record AddLinkRequest(string? Kind, Guid WorkspaceId, Guid ListId, Guid ItemId);

public sealed record RecurrenceRequest(string? Rule);

public sealed record RecurrenceResponse(string Rule, DateOnly? NextDueDate);

/// <summary>A task in a cross-list view (TSK-03).</summary>
public sealed record MyTask(
    Guid WorkspaceId, Guid ListId, string ListName, Guid ItemId, string? Title, string? Status, string? Priority, string? DueDate, IReadOnlyList<Guid> AssignedTo);

public sealed record MyTasksResponse([property: JsonPropertyName("value")] IReadOnlyList<MyTask> Value);

/// <summary>Creates a task about a document (TSK-06): in <c>workspaceId</c>/<c>listId</c> (a task list).</summary>
public sealed record TaskFromDocumentRequest(Guid WorkspaceId, Guid ListId, string? Title, DateOnly? DueDate, IReadOnlyList<Guid>? AssignedTo);

/// <summary>
/// Task features on list items: checklists (TSK-01), subtasks and dependencies (TSK-02), cross-list views (TSK-03),
/// recurrence (TSK-05) and tasks from documents (TSK-06). Access comes from the lists engine (<see cref="IListItemStore"/>):
/// reading needs Read on the item, changing Contribute.
/// </summary>
internal static class TaskEndpoints
{
    public const int MaxChecklistEntries = 200;
    private const int MaxMyTasks = 500;

    public static void Map(IEndpointRouteBuilder app)
    {
        var item = app.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}").WithTags("Tasks");
        item.MapGet("/checklist", GetChecklistAsync).RequireScope(TaskScopes.Read).WithName("GetChecklist");
        item.MapPut("/checklist", ReplaceChecklistAsync).RequireScope(TaskScopes.Write).WithName("ReplaceChecklist")
            .WithDescription("Replaces the checklist (order as given).");
        item.MapGet("/links", GetLinksAsync).RequireScope(TaskScopes.Read).WithName("GetTaskLinks");
        item.MapPost("/links", AddLinkAsync).RequireScope(TaskScopes.Write).WithName("AddTaskLink")
            .WithDescription("Links the task to another item: subtask (one parent per task), blockedBy or document. Cycles are rejected.");
        item.MapDelete("/links/{linkId:guid}", RemoveLinkAsync).RequireScope(TaskScopes.Write).WithName("RemoveTaskLink");
        item.MapGet("/recurrence", GetRecurrenceAsync).RequireScope(TaskScopes.Read).WithName("GetTaskRecurrence");
        item.MapPut("/recurrence", SetRecurrenceAsync).RequireScope(TaskScopes.Write).WithName("SetTaskRecurrence")
            .WithDescription("Makes the task repeat (RFC 5545 RRULE, e.g. FREQ=MONTHLY;BYMONTHDAY=1); the task needs a due date.");
        item.MapDelete("/recurrence", RemoveRecurrenceAsync).RequireScope(TaskScopes.Write).WithName("RemoveTaskRecurrence");
        item.MapGet("/tasks", TasksOfDocumentAsync).RequireScope(TaskScopes.Read).WithName("ListTasksOfDocument");
        item.MapPost("/tasks", CreateTaskFromDocumentAsync).RequireScope(TaskScopes.Write).WithName("CreateTaskFromDocument")
            .WithDescription("Creates a task in a task list, linked to the document (e.g. \"pay this invoice\").");

        app.MapGet("/v1.0/me/tasks", MyTasksAsync).RequireScope(TaskScopes.Read).WithTags("Tasks").WithName("ListMyTasks")
            .WithDescription("Open tasks across every task list the caller can read: view=mine (default), dueThisWeek, overdue or all. Dates are UTC; ordered by due date.");
    }

    // ---- Checklist -----------------------------------------------------------

    private static async Task<Results<Ok<ChecklistResponse>, ProblemHttpResult>> GetChecklistAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, TasksDbContext db, CancellationToken ct)
    {
        if (await TaskAccess.TaskAsync(items, workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        return TypedResults.Ok(new ChecklistResponse(await ChecklistAsync(db, caller.TenantId, itemId, ct)));
    }

    private static async Task<Results<Ok<ChecklistResponse>, ValidationProblem, ProblemHttpResult>> ReplaceChecklistAsync(
        Guid workspaceId, Guid listId, Guid itemId, List<ChecklistEntryDto> entries, Caller caller, IListItemStore items, TasksDbContext database, CancellationToken cancellationToken)
    {
        if (entries.Count > MaxChecklistEntries || entries.Any(e => string.IsNullOrWhiteSpace(e.Text) || e.Text.Length > 500))
        {
            return ApiErrors.Validation("checklist", $"Up to {MaxChecklistEntries} entries with 1 to 500 characters each.");
        }

        var task = await TaskAccess.TaskAsync(items, workspaceId, listId, itemId, cancellationToken);
        if (task is null)
        {
            return ApiErrors.NotFound();
        }

        if (task.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        var db = database;
        var tenant = caller.TenantId;
        var id = itemId;
        var ct = cancellationToken;
        db.Checklist.RemoveRange(await db.Checklist.Where(c => c.TenantId == tenant && c.ItemId == id).ToListAsync(ct));
        db.Checklist.AddRange(entries.Select((e, i) => new ChecklistEntry { Id = Ids.New(), TenantId = tenant, ItemId = id, Position = i, Text = e.Text.Trim(), Done = e.Done }));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ChecklistResponse(await ChecklistAsync(db, tenant, id, ct)));
    }

    internal static async Task<List<ChecklistEntryDto>> ChecklistAsync(TasksDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        var entries = await db.Checklist.AsNoTracking().Where(c => c.TenantId == tenant && c.ItemId == id).OrderBy(c => c.Position).ToListAsync(ct);
        return [.. entries.Select(c => new ChecklistEntryDto(c.Text, c.Done))];
    }

    // ---- Links ---------------------------------------------------------------

    private static async Task<Results<Ok<TaskLinksResponse>, ProblemHttpResult>> GetLinksAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, TasksDbContext database, CancellationToken cancellationToken)
    {
        if (await TaskAccess.TaskAsync(items, workspaceId, listId, itemId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var db = database;
        var tenant = caller.TenantId;
        var id = itemId;
        var document = TaskLinkKinds.Document;
        var ct = cancellationToken;
        var outgoing = await db.Links.AsNoTracking().Where(l => l.TenantId == tenant && l.ItemId == id).OrderBy(l => l.Id).ToListAsync(ct);
        var incoming = await db.Links.AsNoTracking().Where(l => l.TenantId == tenant && l.TargetItemId == id && l.Kind != document).OrderBy(l => l.Id).ToListAsync(ct);

        Task<List<LinkedItem>> TargetsAsync(string kind) =>
            TaskAccess.VisibleAsync(items, outgoing.Where(l => l.Kind == kind).Select(l => (l.Id, l.TargetWorkspaceId, l.TargetListId, l.TargetItemId)), ct);

        Task<List<LinkedItem>> SourcesAsync(string kind) =>
            TaskAccess.VisibleAsync(items, incoming.Where(l => l.Kind == kind).Select(l => (l.Id, l.WorkspaceId, l.ListId, l.ItemId)), ct);

        return TypedResults.Ok(new TaskLinksResponse(
            (await SourcesAsync(TaskLinkKinds.Subtask)).FirstOrDefault(),
            await TargetsAsync(TaskLinkKinds.Subtask),
            await TargetsAsync(TaskLinkKinds.BlockedBy),
            await SourcesAsync(TaskLinkKinds.BlockedBy),
            await TargetsAsync(TaskLinkKinds.Document)));
    }

    private static async Task<Results<Created<LinkedItem>, ValidationProblem, ProblemHttpResult>> AddLinkAsync(
        Guid workspaceId, Guid listId, Guid itemId, AddLinkRequest request, Caller caller, IListItemStore items, TasksDbContext database, CancellationToken cancellationToken)
    {
        if (!TaskLinkKinds.IsValid(request.Kind))
        {
            return ApiErrors.Validation("kind", "Use subtask, blockedBy or document.");
        }

        var kind = request.Kind!;
        var ct = cancellationToken;
        var task = await TaskAccess.TaskAsync(items, workspaceId, listId, itemId, ct);
        if (task is null)
        {
            return ApiErrors.NotFound();
        }

        if (task.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        var target = kind == TaskLinkKinds.Document
            ? await TaskAccess.DocumentAsync(items, request.WorkspaceId, request.ListId, request.ItemId, ct)
            : await TaskAccess.TaskAsync(items, request.WorkspaceId, request.ListId, request.ItemId, ct);
        if (target is null || request.ItemId == itemId)
        {
            return ApiErrors.Validation("itemId", kind == TaskLinkKinds.Document ? "A document you can read is expected." : "Another task you can read is expected.");
        }

        var db = database;
        var tenant = caller.TenantId;
        var source = itemId;
        var targetId = target.Id;
        var subtask = TaskLinkKinds.Subtask;
        if (await db.Links.AnyAsync(l => l.TenantId == tenant && l.ItemId == source && l.TargetItemId == targetId && l.Kind == kind, ct))
        {
            return ApiErrors.Conflict("linkExists", "The items are already linked this way.");
        }

        if (kind == TaskLinkKinds.Subtask && await db.Links.AnyAsync(l => l.TenantId == tenant && l.TargetItemId == targetId && l.Kind == subtask, ct))
        {
            return ApiErrors.Conflict("hasParent", "The task already is a subtask of another task.");
        }

        if (kind != TaskLinkKinds.Document && await ReachesAsync(db, tenant, targetId, source, kind, ct))
        {
            return ApiErrors.Conflict("cycle", "The link would create a cycle.");
        }

        var link = new TaskLink
        {
            Id = Ids.New(),
            TenantId = tenant,
            Kind = kind,
            ItemId = source,
            WorkspaceId = workspaceId,
            ListId = listId,
            TargetItemId = targetId,
            TargetWorkspaceId = target.WorkspaceId,
            TargetListId = target.ListId,
        };
        db.Links.Add(link);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created(
            $"/v1.0/workspaces/{workspaceId}/lists/{listId}/items/{itemId}/links",
            new LinkedItem(link.Id, target.WorkspaceId, target.ListId, target.Id, Text(target, "title"), Text(target, "status")));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveLinkAsync(
        Guid workspaceId, Guid listId, Guid itemId, Guid linkId, Caller caller, IListItemStore items, TasksDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var id = itemId;
        var link = linkId;
        var ct = cancellationToken;
        var task = await TaskAccess.TaskAsync(items, workspaceId, listId, itemId, ct);
        var found = task is null ? null : await db.Links.FirstOrDefaultAsync(l => l.TenantId == tenant && l.Id == link && (l.ItemId == id || l.TargetItemId == id), ct);
        if (task is null || found is null)
        {
            return ApiErrors.NotFound();
        }

        if (task.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        db.Links.Remove(found);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Whether <paramref name="to"/> can be reached from <paramref name="from"/> over links of <paramref name="kind"/>.</summary>
    private static async Task<bool> ReachesAsync(TasksDbContext database, Guid tenantId, Guid from, Guid to, string kind, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var linkKind = kind;
        var ct = cancellationToken;
        var seen = new HashSet<Guid> { from };
        var frontier = new Queue<Guid>([from]);
        while (frontier.TryDequeue(out var node))
        {
            if (node == to)
            {
                return true;
            }

            var current = node;
            foreach (var next in await db.Links.AsNoTracking().Where(l => l.TenantId == tenant && l.Kind == linkKind && l.ItemId == current).Select(l => l.TargetItemId).ToListAsync(ct))
            {
                if (seen.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return false;
    }

    // ---- Recurrence ------------------------------------------------------------

    private static Task<TaskRecurrence?> FindRecurrenceAsync(TasksDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        return db.Recurrences.FirstOrDefaultAsync(r => r.TenantId == tenant && r.ItemId == id, ct);
    }

    private static async Task<Results<Ok<RecurrenceResponse>, ProblemHttpResult>> GetRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, TasksDbContext db, CancellationToken ct)
    {
        var task = await TaskAccess.TaskAsync(items, workspaceId, listId, itemId, ct);
        var recurrence = task is null ? null : await FindRecurrenceAsync(db, caller.TenantId, itemId, ct);
        return recurrence is null
            ? ApiErrors.NotFound("The task does not repeat.")
            : TypedResults.Ok(new RecurrenceResponse(recurrence.Rule, Recurrences.Next(recurrence.Rule, DueDate(task!))));
    }

    private static async Task<Results<Ok<RecurrenceResponse>, ValidationProblem, ProblemHttpResult>> SetRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, RecurrenceRequest request, Caller caller, IListItemStore items, TasksDbContext db, CancellationToken ct)
    {
        var rule = request.Rule?.Trim() ?? string.Empty;
        if (rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            rule = rule[6..];
        }

        if (rule.Length is 0 or > 500 || !Recurrences.IsValid(rule))
        {
            return ApiErrors.Validation("rule", "A valid RRULE is expected, e.g. FREQ=WEEKLY;BYDAY=MO.");
        }

        var task = await TaskAccess.TaskAsync(items, workspaceId, listId, itemId, ct);
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
            return ApiErrors.Validation("dueDate", "A repeating task needs a due date.");
        }

        var recurrence = await FindRecurrenceAsync(db, caller.TenantId, itemId, ct);
        if (recurrence is null)
        {
            recurrence = new TaskRecurrence { Id = Ids.New(), TenantId = caller.TenantId, ItemId = itemId, WorkspaceId = workspaceId, ListId = listId };
            db.Recurrences.Add(recurrence);
        }

        recurrence.Rule = rule;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new RecurrenceResponse(rule, Recurrences.Next(rule, DueDate(task))));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, TasksDbContext db, CancellationToken ct)
    {
        var task = await TaskAccess.TaskAsync(items, workspaceId, listId, itemId, ct);
        if (task is null)
        {
            return ApiErrors.NotFound();
        }

        if (task.Access < WorkspaceAccessLevel.Contribute)
        {
            return TaskAccess.Forbidden();
        }

        if (await FindRecurrenceAsync(db, caller.TenantId, itemId, ct) is { } recurrence)
        {
            db.Recurrences.Remove(recurrence);
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.NoContent();
    }

    // ---- Tasks from documents ----------------------------------------------------

    /// <summary>Tasks linked to a document (TSK-06), those the caller can read.</summary>
    private static async Task<Results<Ok<MyTasksResponse>, ProblemHttpResult>> TasksOfDocumentAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, TasksDbContext database, CancellationToken cancellationToken)
    {
        if (await TaskAccess.DocumentAsync(items, workspaceId, listId, itemId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var db = database;
        var tenant = caller.TenantId;
        var id = itemId;
        var document = TaskLinkKinds.Document;
        var ct = cancellationToken;
        var links = await db.Links.AsNoTracking().Where(l => l.TenantId == tenant && l.TargetItemId == id && l.Kind == document).OrderBy(l => l.Id).ToListAsync(ct);
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

    private static async Task<Results<Created<MyTask>, ValidationProblem, ProblemHttpResult>> CreateTaskFromDocumentAsync(
        Guid workspaceId, Guid listId, Guid itemId, TaskFromDocumentRequest request, Caller caller, IListItemStore items, TasksDbContext db, CancellationToken ct)
    {
        var document = await TaskAccess.DocumentAsync(items, workspaceId, listId, itemId, ct);
        if (document is null)
        {
            return ApiErrors.NotFound();
        }

        var taskList = await items.GetListAsync(request.WorkspaceId, request.ListId, ct);
        if (taskList is null || !taskList.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey))
        {
            return ApiErrors.Validation("listId", "A task list you can see is expected.");
        }

        var fields = new JsonObject
        {
            ["title"] = string.IsNullOrWhiteSpace(request.Title) ? Text(document, "title") ?? "Task" : request.Title,
        };
        if (request.DueDate is { } due)
        {
            fields["dueDate"] = Date(due);
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
            TenantId = caller.TenantId,
            Kind = TaskLinkKinds.Document,
            ItemId = task.Id,
            WorkspaceId = task.WorkspaceId,
            ListId = task.ListId,
            TargetItemId = document.Id,
            TargetWorkspaceId = document.WorkspaceId,
            TargetListId = document.ListId,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/v1.0/workspaces/{task.WorkspaceId}/lists/{task.ListId}/items/{task.Id}", ToMyTask(taskList, task));
    }

    // ---- My tasks ---------------------------------------------------------------

    private static async Task<Results<Ok<MyTasksResponse>, ValidationProblem>> MyTasksAsync(
        string? view, int? top, Caller caller, IListItemStore items, TimeProvider time, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var endOfWeek = today.AddDays(((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7);
        var mine = $"fields/assignedTo/any(a: a eq {caller.UserId})";
        var open = $"fields/status ne '{TaskTemplates.Completed}'";
        var filter = (view ?? "mine") switch
        {
            "mine" => $"{open} and {mine}",
            "dueThisWeek" => $"{open} and {mine} and fields/dueDate ge {Date(today)} and fields/dueDate le {Date(endOfWeek)}",
            "overdue" => $"{open} and {mine} and fields/dueDate lt {Date(today)}",
            "all" => open,
            _ => null,
        };
        if (filter is null)
        {
            return ApiErrors.Validation("view", "Use mine, dueThisWeek, overdue or all.");
        }

        var limit = Math.Clamp(top ?? 100, 1, MaxMyTasks);
        var lists = (await items.GetListsAsync(null, null, ct)).Where(l => l.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey)).ToList();
        if (lists.Count == 0)
        {
            return TypedResults.Ok(new MyTasksResponse([]));
        }

        var (pages, error) = await items.QueryAsync(lists, new ListItemQuery(filter, "fields/dueDate", MaxMyTasks), ct);
        if (error is not null)
        {
            return ApiErrors.Validation("filter", error);
        }

        var result = pages.SelectMany(page => page.Items.Select(task => ToMyTask(page.List, task))).ToList();
        return TypedResults.Ok(new MyTasksResponse([.. result
            .OrderBy(t => t.DueDate ?? "9999-12-31", StringComparer.Ordinal)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .Take(limit)]));
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static DateOnly? DueDate(ListItemData task) =>
        Text(task, "dueDate") is { } due && DateOnly.TryParse(due, CultureInfo.InvariantCulture, out var date) ? date : null;

    internal static string? Text(ListItemData item, string field) => item.Fields[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static MyTask ToMyTask(ListData list, ListItemData task) => new(
        task.WorkspaceId,
        task.ListId,
        list.Name,
        task.Id,
        Text(task, "title"),
        Text(task, "status"),
        Text(task, "priority"),
        Text(task, "dueDate"),
        task.Fields["assignedTo"] is JsonArray assigned ? [.. assigned.Select(a => Guid.Parse(a!.GetValue<string>()))] : []);
}

/// <summary>Resolves tasks and documents with the caller's access, via the lists engine.</summary>
internal static class TaskAccess
{
    public static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    /// <summary>The item when it is readable and its list has the task content type.</summary>
    public static async Task<ListItemData?> TaskAsync(IListItemStore items, Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        return list is null || !list.ContentTypeKeys.Contains(TaskTemplates.ContentTypeKey) ? null : await items.GetAsync(workspaceId, listId, itemId, ct);
    }

    /// <summary>The item when it is readable and in a library.</summary>
    public static async Task<ListItemData?> DocumentAsync(IListItemStore items, Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        return list is not { IsLibrary: true } ? null : await items.GetAsync(workspaceId, listId, itemId, ct);
    }

    /// <summary>The linked items the caller can read.</summary>
    public static async Task<List<LinkedItem>> VisibleAsync(IListItemStore items, IEnumerable<(Guid LinkId, Guid WorkspaceId, Guid ListId, Guid ItemId)> links, CancellationToken ct)
    {
        var result = new List<LinkedItem>();
        foreach (var (linkId, workspaceId, listId, itemId) in links)
        {
            if (await items.GetAsync(workspaceId, listId, itemId, ct) is { } item)
            {
                result.Add(new LinkedItem(linkId, workspaceId, listId, itemId, TaskEndpoints.Text(item, "title"), TaskEndpoints.Text(item, "status")));
            }
        }

        return result;
    }
}
