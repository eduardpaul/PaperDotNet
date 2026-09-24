using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Tenancy.Data;

/// <summary>A tenant (organization). Platform-level: not itself tenant-owned.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }

    /// <summary>URL-safe slug, e.g. <c>acme</c>. Used by host and header resolution.</summary>
    public required string Identifier { get; set; }

    public required string Name { get; set; }

    public TenantStatus Status { get; set; }

    /// <summary>Custom host names mapped to this tenant, e.g. <c>dms.acme.com</c>.</summary>
    public List<string> Hosts { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public TenantSummary ToSummary() => new(Id, Identifier, Name, Status, Hosts, CreatedAt);
}
