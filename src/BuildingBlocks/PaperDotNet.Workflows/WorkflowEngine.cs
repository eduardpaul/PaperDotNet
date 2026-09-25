using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace PaperDotNet.Workflows;

/// <summary>Workflow engine settings (section <c>Workflows</c>).</summary>
public sealed class WorkflowEngineOptions
{
    public const string Section = "Workflows";

    /// <summary>Run workflows in this process (one node per installation, like the job scheduler).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often due timers and scheduled work are picked up; events wake workflows at once.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Adds the WorkflowCore engine with its storage (in the application database, configured by the host
    /// per provider) and a hosted service that starts it after the migrations.
    /// </summary>
    public static IServiceCollection AddPaperDotNetWorkflows(this IServiceCollection services, IConfiguration configuration, Action<WorkflowOptions> storage)
    {
        services.AddOptions<WorkflowEngineOptions>().BindConfiguration(WorkflowEngineOptions.Section);
        var poll = configuration.GetValue($"{WorkflowEngineOptions.Section}:{nameof(WorkflowEngineOptions.PollInterval)}", TimeSpan.FromSeconds(5));
        services.AddWorkflow(options =>
        {
            storage(options);
            options.UsePollInterval(poll);

            // A step that failed (e.g. a write conflict) is tried again soon instead of after the default minute.
            options.UseErrorRetryInterval(TimeSpan.FromSeconds(10));
        });
        services.AddHostedService<WorkflowHostService>();
        return services;
    }

    /// <summary>Registers a workflow definition with the engine at startup.</summary>
    public static IServiceCollection AddPaperDotNetWorkflow<TWorkflow, TData>(this IServiceCollection services)
        where TWorkflow : IWorkflow<TData>, new()
        where TData : new() =>
        services.AddSingleton(new WorkflowRegistration(host => host.RegisterWorkflow<TWorkflow, TData>()));
}

public sealed record WorkflowRegistration(Action<IWorkflowHost> Register);

/// <summary>Prepares the engine's storage before it starts (e.g. creates tables the storage provider does not migrate).</summary>
public sealed record WorkflowStoreInitializer(Func<CancellationToken, Task> InitializeAsync);

/// <summary>Registers the workflows and runs the engine while the application runs.</summary>
internal sealed class WorkflowHostService(
    IWorkflowHost host, IEnumerable<WorkflowRegistration> registrations, IEnumerable<WorkflowStoreInitializer> initializers, IOptions<WorkflowEngineOptions> options)
    : IHostedService
{
    private int _started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var initializer in initializers)
        {
            await initializer.InitializeAsync(cancellationToken);
        }

        foreach (var registration in registrations)
        {
            registration.Register(host);
        }

        if (options.Value.Enabled)
        {
            await host.StartAsync(cancellationToken);
            Interlocked.Exchange(ref _started, 1);
        }
    }

    /// <summary>Stops the engine once: WorkflowCore's stop is not idempotent, and hosts may stop services twice.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
        {
            await host.StopAsync(cancellationToken);
        }
    }
}
