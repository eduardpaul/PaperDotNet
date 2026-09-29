using System.Text.Json.Nodes;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>The item a workflow runs on.</summary>
public sealed record WorkflowItem(Guid WorkspaceId, Guid ListId, Guid ItemId);

/// <summary>Outcome of an action; <see cref="Output"/> is recorded with the run.</summary>
public sealed record WorkflowActivityResult(bool Succeeded, string? Error = null, JsonObject? Output = null)
{
    public static WorkflowActivityResult Ok(JsonObject? output = null) => new(true, Output: output);

    public static WorkflowActivityResult Fail(string error) => new(false, error);
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
}

/// <summary>A trigger an extension offers to workflows (key starts with the extension id).</summary>
public sealed record WorkflowTriggerDefinition(string Key, string Description);

/// <summary>Starts the workflows of an extension trigger (in the background, like item events).</summary>
public interface IWorkflowTriggers
{
    Task RaiseAsync(string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, CancellationToken cancellationToken);
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
}
