using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Tasks.Data;
using PaperDotNet.Tasks.Features;

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
        services.AddModuleDbContext<TasksDbContext>(TasksDbContext.Schema);
        services.AddSingleton(TaskTemplates.ContentType);
        services.AddSingleton(TaskTemplates.List);
        services.AddScoped<TaskAccess>();
        services.AddScoped<IEventSubscriber<ItemUpdated>, RecurringTaskSpawner>();
        services.AddScoped<IEventSubscriber<ItemPurged>, PurgedTaskData>();
        services.AddTenantRecurringJob<DueTaskReminderJob>(DueTaskReminderJob.Name, DueTaskReminderJob.Schedule);
        services.AddScopes(TaskScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => TaskEndpoints.Map(endpoints);
}
