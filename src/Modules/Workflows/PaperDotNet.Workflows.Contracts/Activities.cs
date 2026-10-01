using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>Outcome of an activity: done with an output (and optionally another outcome port than <c>done</c>), or failed.</summary>
public sealed record WorkflowActivityResult(bool Succeeded, string? Error = null, JsonObject? Output = null, string? Outcome = null)
{
    public static WorkflowActivityResult Ok(JsonObject? output = null) => new(true, Output: output);

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
}
