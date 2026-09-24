using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;
using PaperDotNet.Workspaces.Contracts;
using PaperDotNet.Workspaces.Data;
using PaperDotNet.Workspaces.Features;

namespace PaperDotNet.Workspaces;

public sealed class WorkspacesModule : IModule
{
    public string Name => "Workspaces";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<WorkspacesDbContext>(WorkspacesDbContext.Schema);
        services.AddScoped<WorkspaceAccess>();
        services.AddScoped<IWorkspaceAccess>(sp => sp.GetRequiredService<WorkspaceAccess>());
        services.AddScopes(WorkspaceScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => WorkspaceEndpoints.Map(endpoints);
}
