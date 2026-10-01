using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
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
/// Tasks: list items of the <c>task</c> content type; this module adds what fields cannot express (checklists, links,
/// recurrence, cross-list views), the <c>task.create</c> activity and due reminders. The <c>task.completed</c> workflow
/// trigger comes with workflow parity (T14).
/// </summary>
public sealed class TasksModule : IModule
{
    public string Name => "Tasks";

    public IJsonTypeInfoResolver Json => TasksJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TasksDbContext>();
        services.AddSingleton(TaskTemplates.ContentType);
        services.AddSingleton(TaskTemplates.List);
        services.AddWorkflowActivity<TaskCreateActivity>();
        services.AddWorkflowTrigger(new WorkflowTriggerDefinition(WorkflowTriggerKeys.TaskCompleted, "A task was completed (data: completedBy)."));
        services.AddTenantRecurringJob<DueTaskReminderJob>(DueTaskReminderJob.Name, DueTaskReminderJob.Schedule);
        services.AddScopes(TaskScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => TaskEndpoints.Map(endpoints);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<ChecklistEntryDto>))]
[JsonSerializable(typeof(ChecklistResponse))]
[JsonSerializable(typeof(TaskLinksResponse))]
[JsonSerializable(typeof(AddLinkRequest))]
[JsonSerializable(typeof(LinkedItem))]
[JsonSerializable(typeof(RecurrenceRequest))]
[JsonSerializable(typeof(RecurrenceResponse))]
[JsonSerializable(typeof(MyTasksResponse))]
[JsonSerializable(typeof(MyTask))]
[JsonSerializable(typeof(TaskFromDocumentRequest))]
internal sealed partial class TasksJson : JsonSerializerContext;
