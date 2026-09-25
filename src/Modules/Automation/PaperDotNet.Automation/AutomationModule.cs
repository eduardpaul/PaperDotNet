using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Contracts;
using PaperDotNet.Automation.Data;
using PaperDotNet.Automation.Features;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Automation;

public static class AutomationScopes
{
    public const string Read = "automation.read";
    public const string Write = "automation.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See rules, workflows, runs and your approvals.", GrantedToMembers: true),
        new(Write, "Decide approvals, start workflows on items and (as workspace manager) change rules and workflows.", GrantedToMembers: true),
    ];
}

/// <summary>
/// Automation (phase 5b, ADR-0018/0019): rules on item events and extension triggers, workflows with approvals
/// and delays (resumed through durable messages), built-in and extension actions, path templates.
/// </summary>
public sealed class AutomationModule : IModule
{
    public string Name => "Automation";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AutomationDbContext>(AutomationDbContext.Schema);
        services.AddScoped<TokenExpander>();
        services.AddScoped<RecipientResolver>();
        services.AddScoped<ActionCatalog>();
        services.AddScoped<TriggerCatalog>();
        services.AddScoped<ActionExecutor>();
        services.AddScoped<AutomationValidator>();
        services.AddScoped<IAutomationAction, ItemUpdateAction>();
        services.AddScoped<IAutomationAction, ItemFileAction>();
        services.AddScoped<IAutomationAction, TaskCreateAction>();
        services.AddScoped<IAutomationAction, NotifyAction>();
        services.AddScoped<IAutomationAction, WorkflowStartAction>();

        services.AddIntegrationEvent<AutomationTriggerRaised>();
        services.AddScoped<IAutomationTriggers, AutomationTriggerPublisher>();
        services.AddScoped<RuleRunner>();
        services.AddEventSubscriber<ItemAdded, RuleRunner>();
        services.AddEventSubscriber<ItemUpdated, RuleRunner>();
        services.AddEventSubscriber<ItemDeleted, RuleRunner>();
        services.AddEventSubscriber<AutomationTriggerRaised, RuleRunner>();

        services.AddScoped<WorkflowStarter>();
        services.AddScoped<WorkflowInterpreter>();
        services.AddScoped<ApprovalService>();
        services.AddTenantRecurringJob<AutomationTimerJob>(AutomationTimerJob.Name, AutomationTimerJob.Schedule);

        services.AddScoped<ITemplateHandler, AutomationTemplateHandler>();
        services.AddScopes(AutomationScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => AutomationEndpoints.Map(endpoints);
}
