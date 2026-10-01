using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>
/// Outcome of an activity: done with an output (and optionally another outcome port than <c>done</c>), failed, or waiting
/// for something else to complete it (<see cref="Waiting"/>).
/// </summary>
public sealed record WorkflowActivityResult(bool Succeeded, string? Error = null, JsonObject? Output = null, string? Outcome = null)
{
    /// <summary>The durable wait the run enters; null when the activity is done.</summary>
    public WorkflowWait? Waiting { get; init; }

    public static WorkflowActivityResult Ok(JsonObject? output = null) => new(true, Output: output);

    /// <summary>Done, continuing with the port <paramref name="outcome"/>.</summary>
    public static WorkflowActivityResult Ok(string outcome, JsonObject? output = null) => new(true, Output: output, Outcome: outcome);

    /// <summary>
    /// Suspends the run until <see cref="IWorkflowBookmarks.CompleteAsync"/> completes the wait (<paramref name="kind"/>,
    /// <paramref name="key"/>), or until <paramref name="resumeAt"/> if given (then with the outcome <c>timeout</c>). The
    /// completion's payload becomes the node's output, and its <c>outcome</c> (default <c>done</c>) picks the port.
    /// <paramref name="data"/> is kept with the wait (what it is about), for whoever completes it.
    /// </summary>
    public static WorkflowActivityResult Wait(string kind, string key, DateTimeOffset? resumeAt = null, JsonObject? data = null) =>
        new(true) { Waiting = new WorkflowWait(kind, key, resumeAt) { Data = data } };

    /// <summary>
    /// Suspends the run like <see cref="Wait"/>, but when the wait ends the activity runs again with the same execution id
    /// and gets the wait back as <see cref="WorkflowActivityContext.Resumed"/> (its data and the completion's payload): for
    /// activities that finish the work themselves or keep state between polls in the wait's data.
    /// </summary>
    public static WorkflowActivityResult WaitAndRunAgain(string kind, string key, DateTimeOffset? resumeAt = null, JsonObject? data = null) =>
        new(true) { Waiting = new WorkflowWait(kind, key, resumeAt) { Data = data, RunAgain = true } };

    public static WorkflowActivityResult Fail(string error) => new(false, error);
}

/// <summary>What an activity runs with.</summary>
public sealed class WorkflowActivityContext
{
    public required Guid TenantId { get; init; }

    /// <summary>The workflow's workspace: the lists it works with are there.</summary>
    public required Guid WorkspaceId { get; init; }

    public required Guid RunId { get; init; }

    /// <summary>The run's item, if any.</summary>
    public Guid? ListId { get; init; }

    public Guid? ItemId { get; init; }

    /// <summary>The inputs as saved (tokens not expanded).</summary>
    public required JsonObject Inputs { get; init; }

    /// <summary>
    /// A stable id of this execution (the same when the node runs again after a crash): use it as the id of what the
    /// activity creates, so a repeat creates nothing.
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>The author of the changes the activity makes: the organization, one level deeper than the run.</summary>
    public required ChangeActor Actor { get; init; }

    /// <summary>The user who started the run or whose change triggered it, if any.</summary>
    public Guid? StartedBy { get; init; }

    public required IServiceProvider Services { get; init; }

    /// <summary>The wait this execution is back from, when the activity waited with <c>WaitAndRunAgain</c>; else null.</summary>
    public WorkflowResumedWait? Resumed { get; init; }

    /// <summary>Expands tokens in a text (<c>{title}</c>, <c>{var:name}</c>, <c>{step:node.path}</c>, …).</summary>
    public required Func<string, CancellationToken, Task<string>> ExpandAsync { get; init; }

    /// <summary>An input as JSON: a text that is exactly one token gives its value with its type; other text is expanded.</summary>
    public required Func<string, CancellationToken, Task<JsonNode?>> ResolveAsync { get; init; }

    /// <summary>Resolves every text value of an object (nested objects and arrays included) with <see cref="ResolveAsync"/>.</summary>
    public async Task<JsonObject> ResolveObjectAsync(JsonObject values, CancellationToken cancellationToken)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            result[name] = await ResolveNodeAsync(value, cancellationToken);
        }

        return result;
    }

    private async Task<JsonNode?> ResolveNodeAsync(JsonNode? value, CancellationToken cancellationToken) => value switch
    {
        JsonValue text when text.TryGetValue<string>(out var template) => await ResolveAsync(template, cancellationToken),
        JsonObject obj => await ResolveObjectAsync(obj, cancellationToken),
        JsonArray array => new JsonArray([.. await Task.WhenAll(array.Select(e => ResolveNodeAsync(e, cancellationToken)))]),
        _ => value?.DeepClone(),
    };
}

/// <summary>
/// An action workflow nodes can run (<c>item.update</c>, …), registered with
/// <see cref="WorkflowServiceCollectionExtensions.AddWorkflowActivity{T}"/> (modules) — the same extension point as in ADR-0036. It must be safe to run again with the same
/// <see cref="WorkflowActivityContext.ExecutionId"/> (after a crash).
/// </summary>
public interface IWorkflowActivity
{
    string Key { get; }

    string Description { get; }

    /// <summary>Checks the inputs when a workflow is saved.</summary>
    IEnumerable<string> Validate(JsonObject inputs) => [];

    /// <summary>Outcome ports besides <c>done</c> and <c>error</c>.</summary>
    IReadOnlyList<string> Outcomes => [];

    /// <summary>A JSON Schema of the inputs (see <see cref="ActivitySchemas"/>), for editors and the activity catalog; null when not described.</summary>
    JsonObject? InputSchema => null;

    /// <summary>A JSON Schema of the output, which later nodes read as <c>{step:node.name}</c>; null when not described.</summary>
    JsonObject? OutputSchema => null;

    Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken);
}

public static class WorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowActivity<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services)
        where T : class, IWorkflowActivity
    {
        services.AddSingleton<IWorkflowActivity, T>();
        return services;
    }

    /// <summary>A trigger the module raises with <see cref="IWorkflowTriggers"/>, offered in the trigger catalog.</summary>
    public static IServiceCollection AddWorkflowTrigger(this IServiceCollection services, WorkflowTriggerDefinition trigger)
    {
        services.AddSingleton(trigger);
        return services;
    }
}
