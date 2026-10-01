using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Documents.Features;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;

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
/// Documents: files in libraries (DOC-01…03, DOC-10, DOC-11). Items, permissions and events come from the lists engine
/// through Lists.Contracts; an upload only stores the file and raises <c>document.added</c> (ADR-0038).
/// Text, thumbnails, page images and OCR are built-in library workflows (<see cref="DocumentWorkflows"/>).
/// Subscribers: <see cref="PurgedFilesSubscriber"/>.
/// </summary>
public sealed class DocumentsModule : IModule
{
    public string Name => "Documents";

    public IJsonTypeInfoResolver Json => DocumentsJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<DocumentsDbContext>();
        services.AddScopes(DocumentScopes.All);
        services.Configure<DocumentsOptions>(configuration.GetSection(DocumentsOptions.Section));
        services.AddScoped<FileIntake>();
        services.AddScoped<DocumentEvents>();
        services.AddScoped<DocumentService>();
        services.AddScoped<IItemSearchContributor, DocumentSearchContent>();
        services.AddHttpClient(GlmOcr.HttpClientName, (sp, client) => client.Timeout = sp.GetRequiredService<IOptions<DocumentsOptions>>().Value.OcrTimeout);
        services.AddScoped<OcrEngine>();
        services.AddScoped<PageRenderer>();
        services.AddScoped<IItemPageImageSource, DocumentPageImages>();
        services.AddOperationHandler<DocumentOcr>();
        services.AddWorkflowActivity<ReadTextActivity>();
        services.AddWorkflowActivity<ThumbnailActivity>();
        services.AddWorkflowActivity<RenderPagesActivity>();
        services.AddWorkflowActivity<OcrActivity>();
        foreach (var workflow in DocumentWorkflows.All)
        {
            services.AddWorkflow(workflow);
        }

        services.AddTenantRecurringJob<StoredFileCleanupJob>(StoredFileCleanupJob.Name, StoredFileCleanupJob.Schedule);
        services.AddWorkflowTrigger(new WorkflowTriggerDefinition(DocumentTriggers.Added,
            "A file was added to a library: a new document or a new version (data: version, mediaType, fileName, newDocument). Nothing else happens on upload."));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        DocumentEndpoints.Map(endpoints);
        PageImageEndpoints.Map(endpoints);
    }
}

/// <summary>Every type the Documents API serializes (Native AOT, ADR-0039).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DocumentResponse))]
[JsonSerializable(typeof(FileVersionResponse))]
[JsonSerializable(typeof(FileVersionList))]
[JsonSerializable(typeof(LibrarySettingsResponse))]
[JsonSerializable(typeof(LibrarySettingsRequest))]
[JsonSerializable(typeof(IFormFile))]
[JsonSerializable(typeof(OcrFile))]
[JsonSerializable(typeof(GlmGenerateRequest))]
[JsonSerializable(typeof(GlmGenerateResponse))]
internal sealed partial class DocumentsJson : JsonSerializerContext;
