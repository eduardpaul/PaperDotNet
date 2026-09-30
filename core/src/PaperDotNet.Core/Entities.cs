namespace PaperDotNet.Core;

/// <summary>
/// A row that belongs to one tenant. Under Native AOT there are no global query filters (EF Core compiled models do
/// not support them, ADR-0039): every query on a tenant-owned set filters on <see cref="TenantId"/> explicitly, and
/// <see cref="Persistence.CoreSaveChangesInterceptor"/> rejects writes into another tenant.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>
/// Optimistic concurrency version, surfaced as an ETag. Mark the property <c>[ConcurrencyCheck]</c>; the interceptor
/// sets it to 1 on insert and increments it on every update.
/// </summary>
public interface IVersioned
{
    uint Version { get; set; }
}

/// <summary>Creation and modification stamps, set by the interceptor.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }

    Guid? CreatedBy { get; set; }

    DateTimeOffset UpdatedAt { get; set; }

    Guid? UpdatedBy { get; set; }
}
