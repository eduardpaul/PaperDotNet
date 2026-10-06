using System.Text.Json.Nodes;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>The item a workflow runs on.</summary>
public sealed record WorkflowItem(Guid WorkspaceId, Guid ListId, Guid ItemId);

/// <summary>Durable completion notification for coordinating workflows through events and bookmarks.</summary>
public sealed record WorkflowRunFinished : IntegrationEvent
{
    public required Guid RunId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public Guid? CauseEventId { get; init; }
    public required string Status { get; init; }
    public string? Trigger { get; init; }
    public string? Error { get; init; }
}

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
    /// output, and its <c>outcome</c> (default <c>done</c>) picks the port. <paramref name="data"/> is kept with the wait
    /// (what it is about), for whoever completes it.
    /// </summary>
    public static WorkflowActivityResult Wait(string kind, string key, DateTimeOffset? resumeAt = null, JsonObject? data = null) =>
        new(true) { Waiting = new WorkflowWait(kind, key, resumeAt) { Data = data } };

    /// <summary>
    /// Suspends the run like <see cref="Wait"/>, but when the wait is completed (or <paramref name="resumeAt"/> passes) the
    /// activity runs again with the same execution id, and gets the wait back as <see cref="WorkflowActivityContext.Resumed"/>
    /// (its data and the completion's payload) instead of the payload becoming its output: for activities that finish
    /// the work themselves (e.g. with a batch answer) or keep state between polls in the wait's data.
    /// </summary>
    public static WorkflowActivityResult WaitAndRunAgain(string kind, string key, DateTimeOffset? resumeAt = null, JsonObject? data = null) =>
        new(true) { Waiting = new WorkflowWait(kind, key, resumeAt) { RunAgain = true, Data = data } };
}

/// <summary>A durable wait: <see cref="Kind"/> (e.g. <c>{extension id}.batch</c>) and <see cref="Key"/> identify it within the tenant.</summary>
public sealed record WorkflowWait(string Kind, string Key, DateTimeOffset? ResumeAt = null)
{
    /// <summary>Run the activity again when the wait ends (see <see cref="WorkflowActivityResult.WaitAndRunAgain"/>).</summary>
    public bool RunAgain { get; init; }

    /// <summary>What the wait is about, as JSON (e.g. the question a batch should answer); kept with the wait.</summary>
    public JsonObject? Data { get; init; }
}

/// <summary>
/// The wait a run-again activity is back from (<see cref="WorkflowActivityResult.WaitAndRunAgain"/>): its data, and the
/// payload it was completed with (null when completed without one; <c>{ "outcome": "timeout" }</c> when its time passed).
/// </summary>
public sealed record WorkflowResumedWait(string Kind, string Key, JsonObject? Data, JsonObject? Payload)
{
    /// <summary>Whether the wait ended because its time passed, not because it was completed.</summary>
    public bool TimedOut => Payload?["outcome"]?.GetValueKind() == System.Text.Json.JsonValueKind.String && Payload["outcome"]!.GetValue<string>() == "timeout";
}

/// <summary>
/// One question of an AI batch: <see cref="CustomId"/> identifies its answer (the same question is sent once).
/// <see cref="Images"/> are sent with the input for models that read images (e.g. the pages of a photographed receipt).
/// </summary>
public sealed record AiBatchLine(string CustomId, string Model, string Instructions, string Input, JsonObject? Schema, IReadOnlyList<AiBatchImage>? Images = null);

/// <summary>An image of an AI batch line (e.g. <c>image/jpeg</c>).</summary>
public sealed record AiBatchImage(string MediaType, byte[] Content);

/// <summary>The answer to one line (or its error), with the tokens it used.</summary>
public sealed record AiBatchResult(string CustomId, string? Text, long InputTokens = 0, long OutputTokens = 0, string? Error = null);

public enum AiBatchState
{
    /// <summary>Validating, running or finalizing.</summary>
    Running,

    /// <summary>Done; <see cref="AiBatchStatus.Results"/> holds the answers.</summary>
    Completed,

    /// <summary>Failed, expired or cancelled; lines without a result are queued again.</summary>
    Failed,
}

/// <summary>Where a provider's batch is: its state and, when finished, the results it has.</summary>
public sealed record AiBatchStatus(AiBatchState State, IReadOnlyList<AiBatchResult> Results, string? Error = null);

/// <summary>
/// A provider's batch API for AI activities with <c>execution: batch</c> (AI-08, ADR-0036), e.g. the Azure OpenAI or
/// OpenAI Batch API: cheaper, with a separate quota and results within a day. Register one and the <c>ai.batch</c>
/// activity (the "AI batch" workflow) sends the waiting questions in batches; without one, it answers them with the chat
/// model. A batch holds one model's lines.
/// </summary>
public interface IAiBatchClient
{
    /// <summary>Most lines in one batch.</summary>
    int MaxLines => 50_000;

    /// <summary>Submits the lines as one batch tagged with <paramref name="batchId"/>; returns the provider's batch id.</summary>
    Task<string> SubmitAsync(Guid batchId, IReadOnlyList<AiBatchLine> lines, CancellationToken cancellationToken);

    /// <summary>
    /// The provider's id of the batch tagged with <paramref name="batchId"/>, or null when there is none: when the step runs
    /// again after a crash or failure, it adopts a batch it already sent instead of paying for it twice.
    /// </summary>
    Task<string?> FindAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The batch's state, with the results once it has finished.</summary>
    Task<AiBatchStatus> GetAsync(string providerBatchId, CancellationToken cancellationToken);
}

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

    /// <summary>Open waits of a kind in runs of the workspace, oldest first (a page: <paramref name="skip"/>, <paramref name="take"/>).</summary>
    Task<IReadOnlyList<WorkflowOpenWait>> ListOpenAsync(string kind, Guid workspaceId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the data of an open wait (e.g. to mark it as taken by a batch); false when it is no longer open or was
    /// changed meanwhile (read it again).
    /// </summary>
    Task<bool> SetDataAsync(string kind, string key, JsonObject? data, CancellationToken cancellationToken);
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

    /// <summary>The immutable item event that started this run, including its snapshots and actor.</summary>
    public ItemEvent? ItemChange { get; init; }

    /// <summary>Uniform run context: trigger, input, data, workspaceId, listId, itemId, userId and startedAt.</summary>
    public JsonObject? ExecutionContext { get; init; }

    /// <summary>The run the activity is part of (null when an activity runs outside a run).</summary>
    public Guid? RunId { get; init; }

    /// <summary>The wait this execution is back from, when the activity waited with <c>WaitAndRunAgain</c>; else null.</summary>
    public WorkflowResumedWait? Resumed { get; init; }

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

    /// <summary>
    /// The value of an input as JSON: a text that is exactly one token without a format (<c>{step:read.json.total}</c>,
    /// <c>{var:line}</c>, <c>{amount}</c>) gives that value with its type (a number stays a number, a list a list); other
    /// text is expanded like <see cref="ExpandAsync"/>. Null when the token has no value.
    /// </summary>
    public required Func<string, CancellationToken, Task<JsonNode?>> ResolveAsync { get; init; }
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
public sealed record WorkflowTriggerDefinition(string Key, string Description)
{
    /// <summary>Explicit system requests may launch opted-in built-ins while automatic execution is disabled.</summary>
    public bool AllowDisabledBuiltIns { get; init; }

    /// <summary>
    /// Completes a bookmark of this kind, keyed by the request event ID, once every run the request started has ended
    /// (payload: <c>status</c> <c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>none</c> when nothing matched, and
    /// <c>runs</c>). Only these triggers publish <see cref="WorkflowRunFinished"/>, so other runs pay nothing for it.
    /// </summary>
    public string? CompletionKind { get; init; }
}

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

    /// <summary>
    /// Raises the trigger for the event a subscriber handles (e.g. an item event): it is that event again, with its id (a
    /// redelivery starts nothing new) and its causation depth (the loop protection keeps counting). The way to raise a
    /// trigger from an <see cref="IEventSubscriber{TEvent}"/>.
    /// </summary>
    Task RaiseAsync(string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, IntegrationEvent cause, CancellationToken cancellationToken);
}

/// <summary>
/// A workflow the product ships (EVT-12, ADR-0036): read-only, versioned with the release, offered in each workspace's
/// catalog of built-in workflows. A workspace manager enables it with values for its <see cref="Parameters"/>, or copies
/// it into a workflow of their own to change it.
/// </summary>
/// <param name="Key">Unique, starting with the module or extension, e.g. <c>documents.classify</c>.</param>
/// <param name="Name">The name it has in each workspace.</param>
/// <param name="Description">What it does, for the catalog.</param>
/// <param name="Definition">
/// The definition as in the API (<c>trigger</c>, <c>condition</c>, <c>steps</c> or <c>flow</c>, <c>variables</c>). A string
/// that is exactly <c>{param:name}</c> is replaced by the parameter's value (any JSON, e.g. a list of fields, or null);
/// <c>{param:name}</c> inside a longer string or a property name is replaced by its text.
/// </param>
public sealed record BuiltInWorkflow(string Key, string Name, string Description, JsonObject Definition)
{
    /// <summary>The parameters as a JSON Schema object (<c>properties</c> with a <c>type</c>, <c>default</c> and <c>description</c> each, <c>required</c>).</summary>
    public JsonObject? Parameters { get; init; }

    /// <summary>What the server needs for it to be offered as available, e.g. <see cref="BuiltInRequirements.Ai"/>.</summary>
    public string? Requires { get; init; }

    /// <summary>
    /// Where it is turned on: in the workspace (default), or per library (<see cref="BuiltInScope.Library"/>: one per
    /// library, its triggers apply to that library, and <c>{param:list}</c> is the library's name).
    /// </summary>
    public BuiltInScope Scope { get; init; } = BuiltInScope.Workspace;

    /// <summary>On where nobody turned it off: created, turned on, the first time a library (or workspace) needs it.</summary>
    public bool EnabledByDefault { get; init; }

    /// <summary>People can launch this built-in manually even when automatic runs are off in their library.</summary>
    public bool AllowManualLaunch { get; init; }

    /// <summary>
    /// A product process (search indexing, the activity timeline, notifications) rather than a template people adapt. Its
    /// runs are visible and retryable like any other, but cheap: they react to item events at any causation depth (the
    /// activities protect themselves against loops), execute in the system lane so they never wait behind people's
    /// workflows, are kept only <c>Workflows:SystemRunRetentionDays</c> after they succeed, and the workflow is not exported
    /// with templates.
    /// </summary>
    public bool System { get; init; }

    /// <summary>
    /// A process the product needs done (e.g. following a note's links): its role must always have an active workflow. The
    /// built-in is the default; a copy of it or an extension's workflow for the same role may replace it, but the role is
    /// never left without one (turning off the last replacement brings the built-in back).
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// The guarantee itself (e.g. removing deleted items from search, queueing change notifications promised to API
    /// subscribers): it cannot be replaced, copied or turned off, and runs even where its row was turned off.
    /// </summary>
    public bool Locked { get; init; }

    /// <summary>Item events of folders start it too (by default, folders start no workflow).</summary>
    public bool IncludeFolders { get; init; }

    /// <summary>
    /// The process role it fills when it is not the role's default (e.g. an extension's <c>search.index</c> pipeline with AI
    /// context). A role's default built-in is the one whose key is the role; its flags (<see cref="System"/>,
    /// <see cref="Required"/>, <see cref="Locked"/>, <see cref="IncludeFolders"/>) are the role's. One workflow per role is
    /// active in a workspace (per list for list built-ins); turning one on turns the others off.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>The role it fills: <see cref="Role"/>, or its own key for system workflows; null for plain built-ins.</summary>
    public string? RoleKey => Role ?? (IsSystem ? Key : null);

    /// <summary>Whether it is a system workflow (<see cref="System"/>, <see cref="Required"/> or <see cref="Locked"/>).</summary>
    public bool IsSystem => System || Required || Locked;
}

public enum BuiltInScope
{
    Workspace = 0,

    /// <summary>Turned on per document library (e.g. reading the text of its files).</summary>
    Library = 1,

    /// <summary>Turned on per list, including ordinary lists and document libraries.</summary>
    List = 2,
}

public static class BuiltInRequirements
{
    /// <summary>A chat model is configured (<c>AI:Chat</c>).</summary>
    public const string Ai = "ai";
}

/// <summary>Ships built-in workflows: modules register them with <c>services.AddWorkflow(…)</c>, extensions with <c>builder.AddWorkflow(…)</c>.</summary>
public interface IWorkflowDefinitionProvider
{
    IEnumerable<BuiltInWorkflow> GetWorkflows();

    /// <summary>Whether the workflows are offered in the current organization (an extension's only where it is enabled).</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

/// <summary>Built-in trigger types of workflows.</summary>
public static class WorkflowTriggers
{
    /// <summary>Started by a person on selected items or once in a workspace.</summary>
    public const string Manual = "manual";

    /// <summary>Started by an authenticated webhook request with workspace Contribute access.</summary>
    public const string Webhook = "webhook";

    public const string ItemAdded = "itemAdded";
    /// <summary>A managed-metadata tag was assigned to an item.</summary>

    public const string ItemUpdated = "itemUpdated";
    public const string ItemDeleted = "itemDeleted";
    public const string ItemRestored = "itemRestored";

    /// <summary>On a schedule: <c>cron</c> (5 fields) in <c>timeZone</c> (default: the organization's); no item.</summary>
    public const string Schedule = "schedule";

    /// <summary>A set time before or after a date field of the list's items (<c>list</c>, <c>field</c>, <c>offsetHours</c>).</summary>
    public const string Date = "date";

    /// <summary>
    /// A file was added to a library: a new document or a new version of its file (raised by Documents, ADR-0038; data:
    /// <c>version</c>, <c>mediaType</c>, <c>fileName</c>, <c>newDocument</c>). Nothing else happens on upload: text,
    /// thumbnails, page images and OCR are workflows on this trigger.
    /// </summary>
    public const string DocumentAdded = "document.added";

    /// <summary>
    /// Events of workflows start with <c>wf.</c>: <c>wf.{key}.completed</c> and <c>wf.{key}.failed</c> when a run of the
    /// workflow with that key ends, and <c>wf.{key}.{event}</c> from its <c>event.raise</c> steps (ADR-0038).
    /// </summary>
    public const string WorkflowEventPrefix = "wf.";

    /// <summary>An approval of a workflow run was decided (data: <c>workflow</c>, <c>step</c>, <c>outcome</c>, <c>comment</c>).</summary>
    public const string ApprovalDecided = "approval.decided";

    /// <summary>A task was completed (raised by Tasks; data: <c>completedBy</c>).</summary>
    public const string TaskCompleted = "task.completed";

    /// <summary>A comment was added to an item (raised by Collaboration; data: <c>commentId</c>, <c>text</c>, <c>author</c>).</summary>
    public const string CommentAdded = "comment.added";
}
