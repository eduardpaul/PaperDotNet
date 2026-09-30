using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.Abstractions;

/// <summary>
/// A module is a bounded context composed into the host. Its Wolverine subscribers are found in its assembly, and its
/// JSON types come from its source-generated <see cref="Json"/> context (Native AOT, ADR-0039).
/// </summary>
public interface IModule
{
    string Name { get; }

    /// <summary>Metadata of every type the module's endpoints and messages serialize.</summary>
    IJsonTypeInfoResolver? Json => null;

    void AddServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
