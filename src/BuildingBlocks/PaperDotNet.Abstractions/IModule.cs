using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Abstractions;

/// <summary>
/// A module is a bounded context composed into the host. Built-in modules and
/// (later) extensions use the same shape.
/// </summary>
public interface IModule
{
    string Name { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
