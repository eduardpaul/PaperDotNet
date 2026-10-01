using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>
/// A workflow the product ships (EVT-12, ADR-0036): read-only, versioned with the release, offered in each workspace's
/// catalog of built-in workflows. A workspace manager turns it on with values for its <see cref="Parameters"/>, or copies
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
    /// Where it is turned on: in the workspace (default), or per library (<see cref="BuiltInScope.Library"/>: one per library,
    /// its triggers apply to that library, and <c>{param:list}</c> is the library's name).
    /// </summary>
    public BuiltInScope Scope { get; init; } = BuiltInScope.Workspace;

    /// <summary>On where nobody turned it off: created, turned on, the first time a library needs it.</summary>
    public bool EnabledByDefault { get; init; }
}

public enum BuiltInScope
{
    Workspace = 0,

    /// <summary>Turned on per document library (e.g. reading the text of its files).</summary>
    Library = 1,
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

    /// <summary>Whether the workflows are offered in the tenant (an extension's only where it is enabled).</summary>
    ValueTask<bool> IsAvailableAsync(Guid tenantId, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

/// <summary>Whether the server has what built-in workflows need (<see cref="BuiltInWorkflow.Requires"/>); AI modules register one.</summary>
public interface IWorkflowRequirement
{
    string Name { get; }

    bool IsMet { get; }
}

/// <summary>Built-in workflows of a module.</summary>
public sealed class StaticWorkflowProvider(BuiltInWorkflow workflow) : IWorkflowDefinitionProvider
{
    public IEnumerable<BuiltInWorkflow> GetWorkflows() => [workflow];
}

public static class BuiltInWorkflowServiceCollectionExtensions
{
    /// <summary>Ships a built-in workflow (EVT-12): offered in every workspace's catalog.</summary>
    public static IServiceCollection AddWorkflow(this IServiceCollection services, BuiltInWorkflow workflow)
    {
        services.AddSingleton<IWorkflowDefinitionProvider>(new StaticWorkflowProvider(workflow));
        return services;
    }
}
