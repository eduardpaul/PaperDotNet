using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.Workflows;

public static class WorkflowScopes
{
    public const string Read = "workflow.read";
    public const string Write = "workflow.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See workflows, their runs and your approvals.", GrantedToMembers: true),
        new(Write, "Decide approvals, start workflows on items and (as workspace manager) change workflows.", GrantedToMembers: true),
    ];
}

/// <summary>
/// Workflows (ADR-0036; before: automation, ADR-0018/0019/0024): workflows started by item events, extension triggers
/// or people, with actions, approvals, delays and conditions (runs resumed through durable messages), built-in and
/// extension activities, path templates.
/// </summary>
public sealed class WorkflowsModule : IModule
{
    public string Name => "Workflows";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<WorkflowsDbContext>(WorkflowsDbContext.Schema);
        services.AddScoped<TokenExpander>();
        services.AddScoped<RecipientResolver>();
        services.AddScoped<ActionCatalog>();
        services.AddScoped<TriggerCatalog>();
        services.AddScoped<ActionExecutor>();
        services.AddScoped<WorkflowValidator>();
        services.AddScoped<IWorkflowActivity, ItemUpdateAction>();
        services.AddScoped<IWorkflowActivity, ItemFileAction>();
        services.AddScoped<IWorkflowActivity, TaskCreateAction>();
        services.AddScoped<IWorkflowActivity, NotifyAction>();
        services.Configure<WorkflowAiOptions>(configuration.GetSection(WorkflowAiOptions.Section));
        services.Configure<AiBatchOptions>(configuration.GetSection(AiBatchOptions.Section));
        services.AddScoped<AiGateway>();
        services.AddScoped<IWorkflowActivity, AiExtractActivity>();
        services.AddScoped<IWorkflowActivity, AiClassifyActivity>();
        services.AddScoped<IWorkflowActivity, AiSummarizeActivity>();
        services.AddScoped<IWorkflowActivity, AiPromptActivity>();

        services.AddIntegrationEvent<WorkflowTriggerRaised>();
        services.AddScoped<IWorkflowTriggers, WorkflowTriggerPublisher>();
        services.AddEventSubscriber<ItemAdded, WorkflowTriggerHandler>();
        services.AddEventSubscriber<ItemUpdated, WorkflowTriggerHandler>();
        services.AddEventSubscriber<ItemDeleted, WorkflowTriggerHandler>();
        services.AddEventSubscriber<ItemRestored, WorkflowTriggerHandler>();
        services.AddEventSubscriber<WorkflowTriggerRaised, WorkflowTriggerHandler>();

        services.Configure<WorkflowOptions>(configuration.GetSection("Workflows"));
        services.AddScoped<WorkflowStarter>();
        services.AddScoped<WorkflowInterpreter>();
        services.AddScoped<RunService>();
        services.AddScoped<IWorkflowBookmarks, WorkflowBookmarks>();
        services.AddScoped<BuiltInWorkflows>();
        services.AddSingleton<IWorkflowDefinitionProvider, WorkflowBuiltIns>();
        services.AddTenantRecurringJob<BuiltInSyncJob>(BuiltInSyncJob.Name, BuiltInSyncJob.Schedule);
        services.AddTenantRecurringJob<WorkflowTimerJob>(WorkflowTimerJob.Name, WorkflowTimerJob.Schedule);
        services.AddTenantRecurringJob<WorkflowScheduleJob>(WorkflowScheduleJob.Name, WorkflowScheduleJob.Schedule);
        services.AddTenantRecurringJob<WorkflowRunCleanupJob>(WorkflowRunCleanupJob.Name, WorkflowRunCleanupJob.Schedule);
        services.AddTenantRecurringJob<AiBatchSubmitJob>(
            AiBatchSubmitJob.Name, configuration.GetSection(AiBatchOptions.Section).Get<AiBatchOptions>()?.Schedule ?? new AiBatchOptions().Schedule);
        services.AddTenantRecurringJob<AiBatchResultsJob>(AiBatchResultsJob.Name, AiBatchResultsJob.Schedule);

        services.AddScoped<ITemplateHandler, WorkflowTemplateHandler>();
        services.AddScoped<ITemplateHandler, LegacyAutomationTemplateHandler>();
        services.AddScopes(WorkflowScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => WorkflowEndpoints.Map(endpoints);
}
