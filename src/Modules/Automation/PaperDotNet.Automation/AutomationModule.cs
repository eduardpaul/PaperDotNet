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
        new(Read, "See automations, their runs and your approvals.", GrantedToMembers: true),
        new(Write, "Decide approvals, start automations on items and (as workspace manager) change automations.", GrantedToMembers: true),
    ];
}

/// <summary>
/// Automation (phase 5b, ADR-0018/0019/0024): automations started by item events, extension triggers or people, with
/// actions, approvals, delays and conditions (runs resumed through durable messages), built-in and extension actions,
/// path templates.
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

        services.AddIntegrationEvent<AutomationTriggerRaised>();
        services.AddScoped<IAutomationTriggers, AutomationTriggerPublisher>();
        services.AddEventSubscriber<ItemAdded, AutomationTriggerHandler>();
        services.AddEventSubscriber<ItemUpdated, AutomationTriggerHandler>();
        services.AddEventSubscriber<ItemDeleted, AutomationTriggerHandler>();
        services.AddEventSubscriber<ItemRestored, AutomationTriggerHandler>();
        services.AddEventSubscriber<AutomationTriggerRaised, AutomationTriggerHandler>();

        services.Configure<AutomationOptions>(configuration.GetSection("Automation"));
        services.AddScoped<AutomationStarter>();
        services.AddScoped<AutomationInterpreter>();
        services.AddScoped<ApprovalService>();
        services.AddTenantRecurringJob<AutomationTimerJob>(AutomationTimerJob.Name, AutomationTimerJob.Schedule);
        services.AddTenantRecurringJob<AutomationRunCleanupJob>(AutomationRunCleanupJob.Name, AutomationRunCleanupJob.Schedule);

        services.AddScoped<ITemplateHandler, AutomationTemplateHandler>();
        services.AddScopes(AutomationScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => AutomationEndpoints.Map(endpoints);
}
