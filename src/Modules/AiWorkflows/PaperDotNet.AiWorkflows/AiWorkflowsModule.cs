using Microsoft.Extensions.AI;
using PaperDotNet.Abstractions;
using PaperDotNet.AiWorkflows.Data;
using PaperDotNet.AiWorkflows.Features;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.AiWorkflows;

/// <summary>
/// AI in workflows (AI-02…08, ADR-0036): the <c>ai.*</c> activities, batched AI (<c>ai.batch</c> and the built-in "AI batch"
/// workflow) and the record of AI calls. Built on the SDK only (EXT-06): it adds its activities and workflow with the same
/// calls an extension uses. The chat model is an <see cref="IChatClient"/> registered by <c>PaperDotNet.AI</c> when
/// <c>AI:Chat</c> is configured; without one the activities fail with a clear error and the "AI batch" workflow is not
/// offered.
/// </summary>
public sealed class AiWorkflowsModule : IModule
{
    public string Name => "AiWorkflows";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AiWorkflowsDbContext>();
        services.Configure<WorkflowAiOptions>(configuration.GetSection(WorkflowAiOptions.Section));
        services.Configure<AiBatchOptions>(configuration.GetSection(AiBatchOptions.Section));
        services.AddScoped<AiGateway>();
        services.AddSingleton<IWorkflowRequirement>(sp => new ChatModelRequirement(sp.GetService<IChatClient>() is not null));
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

    /// <summary><see cref="BuiltInRequirements.Ai"/>: met when the server has a chat model.</summary>
    private sealed class ChatModelRequirement(bool met) : IWorkflowRequirement
    {
        public string Name => BuiltInRequirements.Ai;

        public bool IsMet => met;
    }
}
