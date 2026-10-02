using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
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
        services.AddSingleton(sp => new TriggerCatalog(sp.GetServices<WorkflowTriggerDefinition>()));
        services.AddScoped<TriggerTerms>();
        services.AddScoped<BuiltInWorkflows>();
        services.AddWorkflow(WorkflowBuiltIns.ApproveItemsWorkflow);
        services.AddTenantRecurringJob<BuiltInSyncJob>(BuiltInSyncJob.Name, BuiltInSyncJob.Schedule);
        services.AddSingleton<IWorkflowTriggers>(sp => new WorkflowTriggerPublisher(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<TimeProvider>()));
        services.AddTenantRecurringJob<WorkflowScheduleJob>(WorkflowScheduleJob.Name, WorkflowScheduleJob.Schedule);
        services.AddScoped<WorkflowInterpreter>();
        services.AddScoped<IWorkflowDirectory>(sp => new WorkflowDirectory(sp.GetRequiredService<WorkflowsDbContext>()));
        services.AddScoped(sp => new RunService(
            sp.GetRequiredService<WorkflowsDbContext>(), sp.GetRequiredService<PaperDotNet.Messaging.IOutbox>(),
            sp.GetRequiredService<PaperDotNet.Collaboration.Contracts.IItemActivity>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IWorkflowBookmarks>(sp => new WorkflowBookmarks(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<TimeProvider>()));
        services.AddTenantRecurringJob<WorkflowTimerJob>(WorkflowTimerJob.Name, WorkflowTimerJob.Schedule);
        services.Configure<WorkflowOptions>(configuration.GetSection(WorkflowOptions.Section));
        services.AddTenantRecurringJob<WorkflowRunCleanupJob>(WorkflowRunCleanupJob.Name, WorkflowRunCleanupJob.Schedule);
        services.AddScoped<ITemplateHandler>(sp => new WorkflowTemplateHandler(
            sp.GetRequiredService<WorkflowsDbContext>(), sp.GetServices<IWorkflowActivity>(), sp.GetRequiredService<TriggerCatalog>(), sp.GetRequiredService<BuiltInWorkflows>(),
            sp.GetRequiredService<WorkflowItems>(), sp.GetRequiredService<TimeProvider>()));
        services.AddWorkflowActivity<ItemCreateActivity>();
        services.AddWorkflowActivity<ItemUpdateActivity>();
        services.AddWorkflowActivity<ItemGetActivity>();
        services.AddWorkflowActivity<ItemsQueryActivity>();
        services.AddWorkflowActivity<ItemDeleteActivity>();
        services.AddWorkflowActivity<ItemFileActivity>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        WorkflowEndpoints.Map(endpoints);
        ApprovalEndpoints.Map(endpoints);
        BuiltInEndpoints.Map(endpoints);
    }
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
[JsonSerializable(typeof(IReadOnlyList<TriggerDto>))]
[JsonSerializable(typeof(List<BuiltInDto>))]
[JsonSerializable(typeof(BuiltInDto))]
[JsonSerializable(typeof(SetBuiltInRequest))]
[JsonSerializable(typeof(CopyBuiltInRequest))]
[JsonSerializable(typeof(List<RunDto>))]
[JsonSerializable(typeof(ResumeRun))]
[JsonSerializable(typeof(NotifyApproval))]
[JsonSerializable(typeof(WorkflowTriggerRaised))]
[JsonSerializable(typeof(ApprovalDto))]
[JsonSerializable(typeof(Page<ApprovalDto>))]
[JsonSerializable(typeof(ApprovalDecisionRequest))]
internal sealed partial class WorkflowsJson : JsonSerializerContext;
