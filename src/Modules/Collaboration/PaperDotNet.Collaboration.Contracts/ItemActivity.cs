namespace PaperDotNet.Collaboration.Contracts;

/// <summary>Kinds of activity entries written by the platform; extensions use their own (<c>{extension id}.…</c>).</summary>
public static class ActivityKinds
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
    public const string Restored = "restored";
    public const string Commented = "commented";
    public const string Approval = "approval";
}

/// <summary>
/// An entry for an item's activity timeline. <see cref="DeduplicationKey"/> makes recording
/// idempotent (e.g. <c>approval:{id}</c>): a second entry with the same key is ignored.
/// </summary>
public sealed record ItemActivityEntry(
    Guid WorkspaceId, Guid ListId, Guid ItemId, string Kind, string? Summary = null, string? DeduplicationKey = null);

/// <summary>
/// Adds entries to the activity timeline of items (LST-17). Item changes and comments are
/// recorded by the platform; modules and extensions add their own events (approvals, signatures, …).
/// The entry names the current user as the actor.
/// </summary>
public interface IItemActivity
{
    Task RecordAsync(ItemActivityEntry entry, CancellationToken cancellationToken);
}
