using Finbuckle.MultiTenant.Abstractions;

namespace PaperDotNet.Tenancy.Resolution;

/// <summary>Tenant information carried by Finbuckle for the current request.</summary>
public sealed record PaperDotNetTenantInfo : ITenantInfo
{
    public required string Id { get; init; }

    public required string Identifier { get; init; }

    public string? Name { get; init; }

    public Guid TenantId => Guid.Parse(Id);
}
