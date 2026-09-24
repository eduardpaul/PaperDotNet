namespace PaperDotNet.Abstractions;

/// <summary>The tenant the current operation runs in.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    string? TenantIdentifier { get; }

    bool IsResolved => TenantId is not null;
}

/// <summary>Marks an entity that belongs to exactly one tenant.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>
/// Runs code inside an explicit tenant (and optionally as a user), for
/// background work, the CLI and bootstrap. Request code gets both from the
/// HTTP request instead.
/// </summary>
public interface ITenantScopeFactory
{
    Microsoft.Extensions.DependencyInjection.AsyncServiceScope CreateScope(Guid tenantId, string tenantIdentifier, Guid? userId = null);
}

/// <summary>
/// Called when a tenant is created, so each module can provision its defaults
/// (e.g. built-in roles). Runs inside the new tenant's scope.
/// </summary>
public interface ITenantInitializer
{
    Task InitializeAsync(Guid tenantId, CancellationToken cancellationToken);
}
