namespace PaperDotNet.Abstractions;

/// <summary>Creation and modification metadata, stamped by the persistence layer.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }

    Guid? CreatedBy { get; set; }

    DateTimeOffset UpdatedAt { get; set; }

    Guid? UpdatedBy { get; set; }
}

/// <summary>Entities that go to the recycle bin instead of being deleted.</summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }

    Guid? DeletedBy { get; set; }
}

/// <summary>
/// Entities with an optimistic-concurrency version, surfaced as an ETag. The
/// persistence layer sets it to 1 on insert and increments it on every update
/// (works on every database provider).
/// </summary>
public interface IVersioned
{
    uint Version { get; set; }
}

public static class Ids
{
    /// <summary>New time-ordered identifier (UUIDv7), good for index locality and keyset paging.</summary>
    public static Guid New() => Guid.CreateVersion7();
}

/// <summary>
/// Excludes an entity type (or a single property) from the audit log, for
/// technical state that changes often (job progress, last-used timestamps).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = false)]
public sealed class NotAuditedAttribute : Attribute;
