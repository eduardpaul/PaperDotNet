using PaperDotNet.Abstractions;

namespace PaperDotNet.Calendar.Data;

/// <summary>One independently refreshed source of one list. The URL is a credential.</summary>
public sealed class CalendarSubscription : ITenantOwned, IVersioned, IAuditable
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ListId { get; set; }
    public required string Name { get; set; }
    [NotAudited]
    public required string ProtectedUrl { get; set; }
    [NotAudited]
    public required string UrlHash { get; set; }
    public bool Paused { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? Error { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public string? HttpETag { get; set; }
    public DateTimeOffset? HttpLastModified { get; set; }
    [NotAudited]
    public Guid? LeaseId { get; set; }
    [NotAudited]
    public DateTimeOffset? LeaseUntil { get; set; }
    public uint Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
