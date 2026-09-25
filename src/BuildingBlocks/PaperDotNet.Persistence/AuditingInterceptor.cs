using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence;

/// <summary>
/// Stamps the tenant, audit columns and concurrency versions on save, turns deletes of
/// <see cref="ISoftDeletable"/> into soft deletes (deleting an already soft-deleted
/// entity purges it), writes <see cref="AuditEntry"/> records in the same
/// transaction, and rejects any write that would touch another tenant's data.
/// </summary>
public sealed class AuditingInterceptor(ITenantContext tenant, ICurrentUser user, TimeProvider time, AuditOverrides overrides) : SaveChangesInterceptor
{
    /// <summary>Columns maintained by this interceptor; never reported as changes.</summary>
    private static readonly HashSet<string> TechnicalProperties =
    [
        nameof(IVersioned.Version), nameof(IAuditable.UpdatedAt), nameof(IAuditable.UpdatedBy),
        nameof(ISoftDeletable.DeletedAt), nameof(ISoftDeletable.DeletedBy), "ConcurrencyStamp",
    ];

    private static readonly ConcurrentDictionary<Type, bool> AuditedTypes = new();
    private static readonly ConcurrentDictionary<Type, string> ModuleNames = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            if (entry.Entity is ITenantOwned owned)
            {
                EnsureTenant(entry.State, owned);
                Audit(context, entry, now);
            }

            // Deleting an entity that is already in the recycle bin purges it.
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable deletable
                && entry.Property(nameof(ISoftDeletable.DeletedAt)).OriginalValue is null)
            {
                entry.State = EntityState.Modified;
                deletable.DeletedAt = now;
                deletable.DeletedBy = user.UserId;
            }

            if (entry.Entity is IVersioned versioned)
            {
                versioned.Version = entry.State == EntityState.Added ? 1 : versioned.Version + 1;
            }

            if (entry.Entity is IAuditable audited)
            {
                // Imports keep the original stamps of what they create (AuditOverrides).
                var stamp = entry.Metadata.FindProperty("Id") is { ClrType: var idType } && idType == typeof(Guid)
                    && overrides.TryGet((Guid)entry.Property("Id").CurrentValue!, out var imported) ? imported : null;
                if (entry.State == EntityState.Added)
                {
                    audited.CreatedAt = stamp?.CreatedAt ?? now;
                    audited.CreatedBy = stamp is null ? user.UserId : stamp.CreatedBy;
                }

                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    audited.UpdatedAt = stamp?.UpdatedAt ?? stamp?.CreatedAt ?? now;
                    audited.UpdatedBy = stamp is null ? user.UserId : stamp.UpdatedBy ?? stamp.CreatedBy;
                }
            }
        }
    }

    private void Audit(DbContext context, EntityEntry entry, DateTimeOffset now)
    {
        var type = entry.Metadata.ClrType;
        if (!AuditedTypes.GetOrAdd(type, t => t.GetCustomAttribute<NotAuditedAttribute>() is null))
        {
            return;
        }

        var softDeletable = entry.Entity is ISoftDeletable;
        var wasDeleted = softDeletable && entry.Property(nameof(ISoftDeletable.DeletedAt)).OriginalValue is not null;
        List<string> properties = [];
        AuditAction action;
        switch (entry.State)
        {
            case EntityState.Added:
                action = AuditAction.Created;
                break;
            case EntityState.Deleted:
                action = softDeletable && !wasDeleted ? AuditAction.Deleted : AuditAction.Purged;
                break;
            case EntityState.Modified when wasDeleted && ((ISoftDeletable)entry.Entity).DeletedAt is null:
                action = AuditAction.Restored;
                break;
            default:
                action = AuditAction.Updated;
                properties = entry.Properties
                    .Where(p => p.IsModified && !TechnicalProperties.Contains(p.Metadata.Name)
                                && p.Metadata.PropertyInfo?.GetCustomAttribute<NotAuditedAttribute>() is null)
                    .Select(p => p.Metadata.Name)
                    .Concat(entry.ComplexProperties.Where(p => p.IsModified).Select(p => p.Metadata.Name))
                    .Concat(entry.ComplexCollections.Where(p => p.IsModified).Select(p => p.Metadata.Name))
                    .ToList();
                if (properties.Count == 0)
                {
                    return;
                }

                break;
        }

        context.Add(new AuditEntry
        {
            Id = Ids.New(),
            TenantId = ((ITenantOwned)entry.Entity).TenantId,
            At = now,
            UserId = user.UserId,
            Action = action,
            EntityType = $"{ModuleName(context.GetType())}.{type.Name}",
            EntityId = entry.Metadata.FindProperty("Id") is { ClrType: var idType } && idType == typeof(Guid) ? (Guid)entry.Property("Id").CurrentValue! : null,
            Properties = properties,
            TraceId = Activity.Current?.TraceId.ToHexString(),
        });
    }

    /// <summary><c>ListsDbContext</c> → <c>lists</c> (stable on every provider, unlike the schema).</summary>
    private static string ModuleName(Type contextType) =>
        ModuleNames.GetOrAdd(contextType, t => (t.Name.EndsWith("DbContext", StringComparison.Ordinal) ? t.Name[..^"DbContext".Length] : t.Name).ToLowerInvariant());

    private void EnsureTenant(EntityState state, ITenantOwned owned)
    {
        var current = tenant.TenantId
            ?? throw new InvalidOperationException("Cannot save tenant-owned data without a resolved tenant.");

        if (state == EntityState.Added && owned.TenantId == Guid.Empty)
        {
            owned.TenantId = current;
        }

        if (owned.TenantId != current)
        {
            throw new InvalidOperationException("Cross-tenant write rejected.");
        }
    }
}
