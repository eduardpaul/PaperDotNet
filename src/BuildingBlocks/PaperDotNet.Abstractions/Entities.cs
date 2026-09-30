namespace PaperDotNet.Abstractions;

public static class Ids
{
    /// <summary>New time-ordered identifier (UUIDv7), good for index locality and keyset paging.</summary>
    public static Guid New() => Guid.CreateVersion7();
}

/// <summary>
/// A row that belongs to one tenant. Under Native AOT there are no global query filters (EF Core compiled models do
/// not support them, ADR-0039): every query on a tenant-owned set filters on <see cref="TenantId"/> explicitly, and the
/// save interceptor (PaperDotNet.Persistence) refuses writes into another tenant.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>
/// Optimistic concurrency version, surfaced as an ETag. Mark the property <c>[ConcurrencyCheck]</c>; the save
/// interceptor sets it to 1 on insert and increments it on every update.
/// </summary>
public interface IVersioned
{
    uint Version { get; set; }
}

/// <summary>Creation and modification stamps, set by the save interceptor.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }

    Guid? CreatedBy { get; set; }

    DateTimeOffset UpdatedAt { get; set; }

    Guid? UpdatedBy { get; set; }
}
