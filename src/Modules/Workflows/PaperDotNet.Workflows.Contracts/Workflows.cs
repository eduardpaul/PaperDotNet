using System.Text.Json.Nodes;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>The item a workflow runs on.</summary>
public sealed record WorkflowItem(Guid WorkspaceId, Guid ListId, Guid ItemId);

/// <summary>
/// Outcome of an activity: done with an <see cref="Output"/> (recorded with the run), failed with an
/// <see cref="Error"/>, or waiting for something else to complete it (<see cref="Wait"/>).
/// </summary>
public sealed record WorkflowActivityResult(bool Succeeded, string? Error = null, JsonObject? Output = null)
{
    /// <summary>The durable wait the run enters; null when the activity is done.</summary>
    public WorkflowWait? Waiting { get; init; }

    /// <summary>The outcome port to continue with (default <c>done</c>); one of <see cref="IWorkflowActivity.Outcomes"/>.</summary>
    public string? Outcome { get; init; }

    public static WorkflowActivityResult Ok(JsonObject? output = null) => new(true, Output: output);

    /// <summary>Done, continuing with the port <paramref name="outcome"/>.</summary>
    public static WorkflowActivityResult Ok(string outcome, JsonObject? output = null) => new(true, Output: output) { Outcome = outcome };

    public static WorkflowActivityResult Fail(string error) => new(false, error);

    /// <summary>
    /// Suspends the run until <see cref="IWorkflowBookmarks.CompleteAsync"/> completes the wait (<paramref name="kind"/>,
    /// <paramref name="key"/>), or until <paramref name="resumeAt"/> if given. The completion's payload becomes the node's
    /// output, and its <c>outcome</c> (default <c>done</c>) picks the port.
    /// </summary>
    public static WorkflowActivityResult Wait(string kind, string key, DateTimeOffset? resumeAt = null) =>
        new(true) { Waiting = new WorkflowWait(kind, key, resumeAt) };
}

/// <summary>A durable wait: <see cref="Kind"/> (e.g. <c>{extension id}.batch</c>) and <see cref="Key"/> identify it within the tenant.</summary>
public sealed record WorkflowWait(string Kind, string Key, DateTimeOffset? ResumeAt = null);

/// <summary>
/// Completes waits of workflow runs from other modules and extensions (ADR-0036): e.g. an AI batch job completing the
/// requests of many runs. Completing is atomic with the message that resumes the run, and harmless to repeat: call it
/// after saving your own state, and again after a crash. A completion that arrives before the run has saved its wait is
/// kept and picked up when the wait is created.
/// </summary>
public interface IWorkflowBookmarks
{
    /// <summary>Completes the wait (<paramref name="kind"/>, <paramref name="key"/>) with a payload; false when it was already completed.</summary>
    Task<bool> CompleteAsync(string kind, string key, JsonObject? payload, CancellationToken cancellationToken);
}

/// <summary>What an action runs with.</summary>
public sealed class WorkflowActivityContext
{
    /// <summary>The workspace of the workflow.</summary>
    public required Guid WorkspaceId { get; init; }

    /// <summary>The item of the run; null for triggers without an item.</summary>
    public WorkflowItem? Item { get; init; }

    /// <summary>The action's inputs as saved (tokens not expanded; use <see cref="ExpandAsync"/>).</summary>
    public required JsonObject Inputs { get; init; }

    /// <summary>Services of the tenant, acting on behalf of the organization (no user).</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>The user who started the run or whose change triggered it, if any.</summary>
    public Guid? UserId { get; init; }

    /// <summary>Data of an extension trigger, if any.</summary>
    public JsonObject? Data { get; init; }

    /// <summary>The run the activity is part of (null when an activity runs outside a run).</summary>
    public Guid? RunId { get; init; }

    /// <summary>Where the action runs, e.g. <c>workflow:File invoices</c>.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// A stable key of this action execution (the same when the step runs again after a failure): use it to make
    /// the action safe to repeat, e.g. as a notification deduplication key or to find what an earlier attempt created.
    /// </summary>
    public required string ExecutionKey { get; init; }

    /// <summary>
    /// A stable id of this action execution (the same when the step runs again): use it as the id of what the action
    /// creates, e.g. <c>IListItemStore.CreateAsync(workspaceId, listId, ExecutionId, …)</c>, so a repeat creates nothing.
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>
    /// Replaces tokens in a text: <c>{title}</c>, <c>{fieldName}</c>, <c>{fieldName:format}</c>,
    /// <c>{created:yyyy}</c>, <c>{modified}</c>, <c>{today:yyyy-MM-dd}</c>, <c>{list}</c>, <c>{id}</c>,
    /// <c>{outcome:Step name}</c> and <c>{data:name}</c>. <c>{{</c> and <c>}}</c> are literal braces.
    /// </summary>
    public required Func<string, CancellationToken, Task<string>> ExpandAsync { get; init; }
}

/// <summary>
/// An action that workflow steps can run (EVT-07…09). Built-in keys are like <c>item.update</c>;
/// extension keys start with the extension id. Actions must be safe to run again with the same
/// <see cref="WorkflowActivityContext.ExecutionKey"/> (rare: after a crash or a failed save).
/// </summary>
public interface IWorkflowActivity
{
    string Key { get; }

    string Description { get; }

    /// <summary>Checks the inputs when a workflow is saved (tokens are not expanded yet).</summary>
    IEnumerable<string> Validate(JsonObject inputs) => [];

    Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken);

    /// <summary>The inputs as JSON Schema, for forms and the catalog (<c>GET /v1.0/workflows/activities</c>); null when not described.</summary>
    JsonObject? InputSchema => null;

    /// <summary>The output as JSON Schema; null when not described.</summary>
    JsonObject? OutputSchema => null;

    /// <summary>Outcome ports besides <c>done</c> and <c>error</c> (see <see cref="WorkflowActivityResult.Outcome"/>).</summary>
    IReadOnlyList<string> Outcomes => [];
}

/// <summary>A trigger an extension offers to workflows (key starts with the extension id).</summary>
public sealed record WorkflowTriggerDefinition(string Key, string Description);

/// <summary>Starts the workflows of an extension trigger (in the background, like item events).</summary>
public interface IWorkflowTriggers
{
    Task RaiseAsync(string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, CancellationToken cancellationToken) =>
        RaiseAsync(triggerKey, workspaceId, item, data, Guid.CreateVersion7(), cancellationToken);

    /// <summary>
    /// Raises the trigger as the event <paramref name="eventId"/>: raising it again with the same id starts nothing new
    /// (use the id of what caused it, e.g. the item event a subscriber handles, so a redelivery is harmless).
    /// </summary>
    Task RaiseAsync(string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, Guid eventId, CancellationToken cancellationToken);
}

/// <summary>Built-in trigger types of workflows.</summary>
public static class WorkflowTriggers
{
    /// <summary>Started on an item by a person (<c>POST …/items/{id}/workflows</c>).</summary>
    public const string Manual = "manual";

    public const string ItemAdded = "itemAdded";
    public const string ItemUpdated = "itemUpdated";
    public const string ItemDeleted = "itemDeleted";
    public const string ItemRestored = "itemRestored";

    /// <summary>On a schedule: <c>cron</c> (5 fields) in <c>timeZone</c> (default: the organization's); no item.</summary>
    public const string Schedule = "schedule";

    /// <summary>A set time before or after a date field of the list's items (<c>list</c>, <c>field</c>, <c>offsetHours</c>).</summary>
    public const string Date = "date";

    /// <summary>A document's file was processed: text extracted and OCR done (raised by Documents; data: <c>version</c>, <c>pageCount</c>, <c>ocr</c>).</summary>
    public const string DocumentProcessed = "document.processed";

    /// <summary>An approval of a workflow run was decided (data: <c>workflow</c>, <c>step</c>, <c>outcome</c>, <c>comment</c>).</summary>
    public const string ApprovalDecided = "approval.decided";

    /// <summary>A task was completed (raised by Tasks; data: <c>completedBy</c>).</summary>
    public const string TaskCompleted = "task.completed";

    /// <summary>A comment was added to an item (raised by Collaboration; data: <c>commentId</c>, <c>text</c>, <c>author</c>).</summary>
    public const string CommentAdded = "comment.added";
}
