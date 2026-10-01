using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workspaces.Contracts;
using PaperDotNet.Workspaces.Data;
using PaperDotNet.Workspaces.Features;

namespace PaperDotNet.Workspaces;

/// <summary>Workspaces with owners, members and visitors; <see cref="IWorkspaceAccess"/> for other modules.</summary>
public sealed class WorkspacesModule : IModule
{
    public string Name => "Workspaces";

    public IJsonTypeInfoResolver Json => WorkspacesJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<WorkspacesDbContext>();
        services.AddScopes(WorkspaceScopes.All);
        services.AddScoped<WorkspaceAccess>();
        services.AddScoped<IWorkspaceAccess>(sp => sp.GetRequiredService<WorkspaceAccess>());
        services.AddScoped<ITemplateContainer>(sp => new WorkspaceTemplateContainer(
            sp.GetRequiredService<WorkspacesDbContext>(), sp.GetRequiredService<WorkspaceAccess>(), sp.GetRequiredService<IUserDirectory>()));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => WorkspaceEndpoints.Map(endpoints);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WorkspaceResponse))]
[JsonSerializable(typeof(Page<WorkspaceResponse>))]
[JsonSerializable(typeof(CreateWorkspaceRequest))]
[JsonSerializable(typeof(UpdateWorkspaceRequest))]
[JsonSerializable(typeof(WorkspaceMemberResponse))]
[JsonSerializable(typeof(List<WorkspaceMemberResponse>))]
[JsonSerializable(typeof(AddWorkspaceMemberRequest))]
[JsonSerializable(typeof(PrincipalDeleted))]
internal sealed partial class WorkspacesJson : JsonSerializerContext;
