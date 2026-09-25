using System.Text.Json.Nodes;

namespace PaperDotNet.Automation.Contracts;

/// <summary>The item an automation runs on.</summary>
public sealed record AutomationItem(Guid WorkspaceId, Guid ListId, Guid ItemId);

/// <summary>Outcome of an action; <see cref="Output"/> is recorded with the run.</summary>
public sealed record AutomationActionResult(bool Succeeded, string? Error = null, JsonObject? Output = null)
{
    public static AutomationActionResult Ok(JsonObject? output = null) => new(true, Output: output);

    public static AutomationActionResult Fail(string error) => new(false, error);
}

/// <summary>What an action runs with.</summary>
public sealed class AutomationActionContext
{
    /// <summary>The workspace of the automation.</summary>
    public required Guid WorkspaceId { get; init; }

    /// <summary>The item of the run; null for triggers without an item.</summary>
    public AutomationItem? Item { get; init; }

    /// <summary>The action's inputs as saved (tokens not expanded; use <see cref="ExpandAsync"/>).</summary>
    public required JsonObject Inputs { get; init; }

    /// <summary>Services of the tenant, acting on behalf of the organization (no user).</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>The user who started the run or whose change triggered it, if any.</summary>
    public Guid? UserId { get; init; }

    /// <summary>Data of an extension trigger, if any.</summary>
    public JsonObject? Data { get; init; }

    /// <summary>Where the action runs, e.g. <c>automation:File invoices</c>.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// A stable key of this action execution (the same when the step runs again after a failure): use it to make
    /// the action safe to repeat, e.g. as a notification deduplication key or to find what an earlier attempt created.
    /// </summary>
    public required string ExecutionKey { get; init; }

    /// <summary>
    /// Replaces tokens in a text: <c>{title}</c>, <c>{fieldName}</c>, <c>{fieldName:format}</c>,
    /// <c>{created:yyyy}</c>, <c>{modified}</c>, <c>{today:yyyy-MM-dd}</c>, <c>{list}</c>, <c>{id}</c>,
    /// <c>{outcome:Step name}</c> and <c>{data:name}</c>. <c>{{</c> and <c>}}</c> are literal braces.
    /// </summary>
    public required Func<string, CancellationToken, Task<string>> ExpandAsync { get; init; }
}

/// <summary>
/// An action that automation steps can run (EVT-07…09). Built-in keys are like <c>item.update</c>;
/// extension keys start with the extension id. Actions must be safe to run again with the same
/// <see cref="AutomationActionContext.ExecutionKey"/> (rare: after a crash or a failed save).
/// </summary>
public interface IAutomationAction
{
    string Key { get; }

    string Description { get; }

    /// <summary>Checks the inputs when an automation is saved (tokens are not expanded yet).</summary>
    IEnumerable<string> Validate(JsonObject inputs) => [];

    Task<AutomationActionResult> ExecuteAsync(AutomationActionContext context, CancellationToken cancellationToken);
}

/// <summary>A trigger an extension offers to automations (key starts with the extension id).</summary>
public sealed record AutomationTriggerDefinition(string Key, string Description);

/// <summary>Starts the automations of an extension trigger (in the background, like item events).</summary>
public interface IAutomationTriggers
{
    Task RaiseAsync(string triggerKey, Guid workspaceId, AutomationItem? item, JsonObject? data, CancellationToken cancellationToken);
}

/// <summary>Built-in trigger types of automations.</summary>
public static class AutomationTriggers
{
    /// <summary>Started on an item by a person (<c>POST …/items/{id}/automations</c>).</summary>
    public const string Manual = "manual";

    public const string ItemAdded = "itemAdded";
    public const string ItemUpdated = "itemUpdated";
    public const string ItemDeleted = "itemDeleted";
    public const string ItemRestored = "itemRestored";
}
