using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Tasks.Data;
using PaperDotNet.Tasks.Features;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Tasks;

public static class TaskScopes
{
    public const string Read = "task.read";
    public const string Write = "task.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See tasks, checklists, links and recurrences.", GrantedToMembers: true),
        new(Write, "Change checklists, links and recurrences of tasks.", GrantedToMembers: true),
    ];
}

/// <summary>
/// Tasks (phase 4a). Tasks are list items of the <c>task</c> content type; this module adds what
/// fields cannot express. Built on the extension SDK only (EXT-06).
/// </summary>
public sealed class TasksModule : IModule
{
    public string Name => "Tasks";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddWorkflowActivity<RecurringTaskSpawner>();
        services.AddWorkflow(ItemChangeWorkflows.Reaction("tasks.nextOccurrence", "Create next recurring task", "Creates the next occurrence after a repeating task completes.", "tasks.nextOccurrence",
            [WorkflowTriggers.ItemUpdated], TaskTemplates.ContentTypeKey));
        services.AddModuleDbContext<TasksDbContext>(TasksDbContext.Schema);
        services.AddScoped<IItemMoveParticipant, TasksItemMoveParticipant>();
        services.AddSingleton(TaskTemplates.ContentType);
        services.AddSingleton(TaskTemplates.List);
        services.AddScoped<TaskAccess>();
        services.AddEventSubscriber<ItemPurged, PurgedTaskData>();
        services.AddWorkflowActivity<TaskCompletedTrigger>();
        services.AddWorkflow(ItemChangeWorkflows.Reaction("tasks.completed", "Announce completed tasks", "Announces completed tasks to following workflows.", "tasks.raiseCompleted",
            [WorkflowTriggers.ItemUpdated], TaskTemplates.ContentTypeKey));
        services.AddWorkflowTrigger(new WorkflowTriggerDefinition(WorkflowTriggers.TaskCompleted, "A task was completed (data: completedBy)."));
        services.AddWorkflowActivity<TaskCreateActivity>();
        services.AddTenantRecurringJob<DueTaskReminderJob>(DueTaskReminderJob.Name, DueTaskReminderJob.Schedule);
        services.AddScopes(TaskScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => TaskEndpoints.Map(endpoints);
}
