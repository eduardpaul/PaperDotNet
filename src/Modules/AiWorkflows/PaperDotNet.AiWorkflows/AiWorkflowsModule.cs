using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.AiWorkflows.Data;
using PaperDotNet.AiWorkflows.Features;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.AiWorkflows;

/// <summary>
/// AI in workflows (AI-02…08, ADR-0036): the <c>ai.*</c> activities, batched AI (<c>ai.batch</c> and the built-in "AI batch"
/// workflow) and the record of AI calls. Built on the extension SDK only (EXT-06): it adds its activities and workflow
/// with the same calls an extension uses, so the SDK covers what the product's own workflows need.
/// </summary>
public sealed class AiWorkflowsModule : IModule
{
    public string Name => "AiWorkflows";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AiWorkflowsDbContext>(AiWorkflowsDbContext.Schema);
        services.Configure<WorkflowAiOptions>(configuration.GetSection(WorkflowAiOptions.Section));
        services.Configure<AiBatchOptions>(configuration.GetSection(AiBatchOptions.Section));
        services.AddScoped<AiGateway>();
        services.AddWorkflowActivity<AiExtractActivity>();
        services.AddWorkflowActivity<AiClassifyActivity>();
        services.AddWorkflowActivity<AiSummarizeActivity>();
        services.AddWorkflowActivity<AiPromptActivity>();
        services.AddWorkflowActivity<AiBatchActivity>();
        services.AddWorkflow(AiBatch.Workflow);
        services.AddTenantRecurringJob<AiCallCleanupJob>(AiCallCleanupJob.Name, AiCallCleanupJob.Schedule);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
