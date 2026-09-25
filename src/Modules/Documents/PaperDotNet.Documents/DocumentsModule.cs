using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Documents.Features;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Documents;

public static class DocumentScopes
{
    public const string Read = "document.read";
    public const string Write = "document.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Download documents and their versions.", GrantedToMembers: true),
        new(Write, "Upload documents and new file versions.", GrantedToMembers: true),
    ];
}

/// <summary>
/// Documents (phase 3): files in libraries. Built on the extension SDK only (EXT-06): items,
/// permissions and events come from the lists engine through Lists.Contracts.
/// </summary>
public sealed class DocumentsModule : IModule
{
    public string Name => "Documents";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<DocumentsDbContext>(DocumentsDbContext.Schema);
        services.AddOptions<DocumentsOptions>().BindConfiguration(DocumentsOptions.Section);
        services.AddScoped<FileIntake>();
        services.AddScoped<DocumentService>();
        services.AddScoped<ProcessingScheduler>();
        services.AddScoped<OcrEngine>();
        services.AddScoped<PageRenderer>();
        services.AddOperationHandler<DocumentProcessor>();
        services.AddScoped<IItemSearchContributor, DocumentSearchContent>();
        services.AddEventSubscriber<ItemPurged, PurgedItemFiles>();
        services.AddTenantRecurringJob<StoredFileCleanupJob>(StoredFileCleanupJob.Name, StoredFileCleanupJob.Schedule);
        services.AddScoped<ITemplateHandler, LibrarySettingsTemplateHandler>();
        services.AddScopes(DocumentScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => DocumentEndpoints.Map(endpoints);
}
