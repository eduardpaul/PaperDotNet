using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Documents.Features.PhotoToDocument;

internal static class PhotoToDocumentRegistration
{
    public static IServiceCollection AddPhotoToDocumentFeature(this IServiceCollection services)
    {
        services.AddModuleDbContext<PhotoConversionDbContext>(PhotoConversionDbContext.Schema);
        services.AddScoped<PhotoConversionStore>();
        services.AddScoped<IStagedDocumentRetention, PhotoConversionRetention>();
        services.AddSingleton<PhotoCompositionGate>();
        services.AddWorkflowActivity<ComposePhotosActivity>();
        services.AddWorkflowActivity<AcceptCompositionActivity>();
        services.AddWorkflowActivity<DiscardCompositionActivity>();
        services.AddScoped<IApprovalReviewProvider, CompositionReview>();
        services.AddWorkflow(PhotoToDocument.Workflow);
        return services;
    }
}
