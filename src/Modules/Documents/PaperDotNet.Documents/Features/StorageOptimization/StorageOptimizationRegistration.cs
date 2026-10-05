using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Documents.Features.StorageOptimization;

internal static class StorageOptimizationRegistration
{
    public static IServiceCollection AddStorageOptimizationFeature(this IServiceCollection services)
    {
        services.AddSingleton<ImageOptimizationGate>();
        services.AddScoped<ImageTextAnalysis>();
        services.AddScoped<IDocumentOptimizationAdapter, ImageOptimizationAdapter>();
        services.AddWorkflowActivity<PrepareOptimization>();
        services.AddWorkflowActivity<AcceptOptimization>();
        services.AddWorkflowActivity<DiscardOptimization>();
        services.AddScoped<IApprovalReviewProvider, OptimizationReview>();
        services.AddWorkflow(StorageOptimizationWorkflows.Workflow);
        return services;
    }
}
