using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>A durable wait: <see cref="Kind"/> (e.g. <c>{extension id}.payment</c>) and <see cref="Key"/> identify it within the tenant.</summary>
public sealed record WorkflowWait(string Kind, string Key, DateTimeOffset? ResumeAt = null)
{
    /// <summary>Run the activity again when the wait ends (see <see cref="WorkflowActivityResult.WaitAndRunAgain"/>).</summary>
    public bool RunAgain { get; init; }

    /// <summary>What the wait is about, as JSON (e.g. the question a batch should answer); kept with the wait.</summary>
    public JsonObject? Data { get; init; }
}

/// <summary>
/// The wait a run-again activity is back from: its data, and the payload it was completed with (null when completed
/// without one; <c>{ "outcome": "timeout" }</c> when its time passed).
/// </summary>
public sealed record WorkflowResumedWait(string Kind, string Key, JsonObject? Data, JsonObject? Payload)
{
    /// <summary>Whether the wait ended because its time passed, not because it was completed.</summary>
    public bool TimedOut => Payload?["outcome"] is JsonValue outcome && outcome.GetValueKind() == JsonValueKind.String && outcome.GetValue<string>() == "timeout";
}

/// <summary>An open wait of a run, for whoever completes waits of its kind (e.g. a batch job).</summary>
public sealed record WorkflowOpenWait(string Key, Guid RunId, JsonObject? Data, DateTimeOffset CreatedAt, DateTimeOffset? ResumeAt);

/// <summary>
/// Completes waits of workflow runs from other modules and extensions (ADR-0036). Completing is atomic with the message
/// that resumes the run, and harmless to repeat: call it after saving your own state, and again after a crash. A
/// completion that arrives before the run has saved its wait is kept and picked up when the wait is created. The engine's
/// own kinds (<c>approval</c>, <c>delay</c>, <c>retry</c>) cannot be completed from outside.
/// </summary>
public interface IWorkflowBookmarks
{
    /// <summary>Completes the wait (<paramref name="kind"/>, <paramref name="key"/>) with a payload; false when it was already completed.</summary>
    Task<bool> CompleteAsync(Guid tenantId, string kind, string key, JsonObject? payload, CancellationToken cancellationToken);

    /// <summary>Open waits of a kind in runs of the workspace, oldest first (a page: <paramref name="skip"/>, <paramref name="take"/>).</summary>
    Task<IReadOnlyList<WorkflowOpenWait>> ListOpenAsync(Guid tenantId, string kind, Guid workspaceId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Replaces the data of an open wait (e.g. to mark it as taken by a batch); false when it is no longer open or was changed meanwhile.</summary>
    Task<bool> SetDataAsync(Guid tenantId, string kind, string key, JsonObject? data, CancellationToken cancellationToken);
}
