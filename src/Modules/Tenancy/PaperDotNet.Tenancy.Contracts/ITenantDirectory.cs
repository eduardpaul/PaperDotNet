namespace PaperDotNet.Tenancy.Contracts;

public enum TenantStatus
{
    Active = 0,
    Suspended = 1,
}

public sealed record TenantSummary(
    Guid Id,
    string Identifier,
    string Name,
    TenantStatus Status,
    IReadOnlyList<string> Hosts,
    DateTimeOffset CreatedAt);

/// <summary>Platform-level tenant management, used by the CLI, bootstrap and operators.</summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken);

    Task<TenantSummary?> FindAsync(string identifier, CancellationToken cancellationToken);

    /// <summary>Creates a tenant and runs every module's tenant initializer inside it.</summary>
    Task<TenantSummary> CreateAsync(string identifier, string name, IReadOnlyList<string> hosts, CancellationToken cancellationToken);

    Task SetStatusAsync(string identifier, TenantStatus status, CancellationToken cancellationToken);

    /// <summary>Replaces the custom host names mapped to the tenant (e.g. <c>dms.acme.com</c>).</summary>
    Task SetHostsAsync(string identifier, IReadOnlyList<string> hosts, CancellationToken cancellationToken);
}
