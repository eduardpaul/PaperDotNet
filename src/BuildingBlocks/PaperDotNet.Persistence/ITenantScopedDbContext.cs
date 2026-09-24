namespace PaperDotNet.Persistence;

/// <summary>
/// Implemented by every module DbContext. The tenant query filter reads
/// <see cref="CurrentTenantId"/> from the context instance, so EF evaluates it
/// per query instead of baking a value into the cached model.
/// </summary>
public interface ITenantScopedDbContext
{
    Guid? CurrentTenantId { get; }
}
