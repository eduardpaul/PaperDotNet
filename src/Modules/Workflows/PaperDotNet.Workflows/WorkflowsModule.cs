using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.Workflows;

/// <summary>
/// Workflows (ADR-0036): the engine, its built-in activities (<c>item.create</c>, <c>item.update</c>) and the API.
/// Subscribers: <see cref="WorkflowTriggerSubscriber"/> (item events) and <see cref="WorkflowRunSubscriber"/> (runs).
/// </summary>
public sealed class WorkflowsModule : IModule
{
    public string Name => "Workflows";

    public IJsonTypeInfoResolver Json => WorkflowsJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<WorkflowsDbContext>();
        services.AddScopes(WorkflowScopes.All);
        services.Configure<WorkflowScriptOptions>(configuration.GetSection("Workflows:Scripts"));
        services.AddSingleton<TokenExpander>();
        services.AddScoped<WorkflowItems>();
        services.AddScoped<IWorkflowRecipients, WorkflowRecipientResolver>();
        services.AddScoped<ItemConditions>();
        services.AddScoped<ScriptRunner>();
        services.AddScoped<WorkflowStarter>();
        services.AddScoped<WorkflowInterpreter>();
        services.AddWorkflowActivity<ItemCreateActivity>();
        services.AddWorkflowActivity<ItemUpdateActivity>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => WorkflowEndpoints.Map(endpoints);
}

/// <summary>Every type the Workflows API and its messages serialize (Native AOT, ADR-0039).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WorkflowDto))]
[JsonSerializable(typeof(Page<WorkflowDto>))]
[JsonSerializable(typeof(CreateWorkflowRequest))]
[JsonSerializable(typeof(UpdateWorkflowRequest))]
[JsonSerializable(typeof(StartRunRequest))]
[JsonSerializable(typeof(RunDto))]
[JsonSerializable(typeof(Page<RunDto>))]
[JsonSerializable(typeof(IReadOnlyList<ActivityDto>))]
[JsonSerializable(typeof(ResumeRun))]
internal sealed partial class WorkflowsJson : JsonSerializerContext;
