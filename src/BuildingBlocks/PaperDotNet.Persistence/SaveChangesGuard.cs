using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence;

/// <summary>
/// Stamps versions and audit fields, turns deletes of <see cref="ISoftDeletable"/> entities into soft deletes, and
/// guards tenant isolation on every save. Reads cannot be guarded here (no global
/// query filters under Native AOT, ADR-0039), so every query filters on the tenant itself; writes are checked here:
/// an added row gets the caller's tenant, and a row of another tenant can never be written in a request.
/// Changes of tenant-owned entities (except <see cref="INotAudited"/> ones) go to the audit log (LST-14) in the same
/// transaction, through the build's <see cref="IAuditLogWriter"/>.
/// </summary>
public sealed class SaveChangesGuard(ICurrentUser user, TimeProvider time, AuditOverrides overrides, IAuditLogWriter? auditLog = null) : SaveChangesInterceptor
{
    /// <summary>Columns maintained here or on every use (API tokens); never reported as changes.</summary>
    private static readonly HashSet<string> TechnicalProperties =
    [
        nameof(IVersioned.Version), nameof(IAuditable.UpdatedAt), nameof(IAuditable.UpdatedBy),
        nameof(ISoftDeletable.DeletedAt), nameof(ISoftDeletable.DeletedBy), "ConcurrencyStamp", "LastUsedAt",
    ];

    private static readonly ConcurrentDictionary<Type, string> ModuleNames = new();

    /// <summary>Transactions this guard began for the audit records of a save, committed or rolled back with it.</summary>
    private readonly Dictionary<DbContext, IDbContextTransaction> _transactions = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db && Apply(db) is { Count: > 0 } records && auditLog is not null)
        {
            if (db.Database.CurrentTransaction is null)
            {
                Own(db, db.Database.BeginTransaction());
            }

            try
            {
                auditLog.Write(db, records);
            }
            catch
            {
                Release(db)?.Dispose();
                throw;
            }
        }

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db && Apply(db) is { Count: > 0 } records && auditLog is not null)
        {
            if (db.Database.CurrentTransaction is null)
            {
                Own(db, await db.Database.BeginTransactionAsync(cancellationToken));
            }

            try
            {
                await auditLog.WriteAsync(db, records, cancellationToken);
            }
            catch
            {
                if (Release(db) is { } transaction)
                {
                    await transaction.DisposeAsync();
                }

                throw;
            }
        }

        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Release(eventData.Context) is { } transaction)
        {
            using (transaction)
            {
                transaction.Commit();
            }
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Release(eventData.Context) is { } transaction)
        {
            await using (transaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }

        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Release(eventData.Context)?.Dispose();

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Release(eventData.Context) is { } transaction)
        {
            await transaction.DisposeAsync();
        }
    }

    // A concurrency conflict is not reported as a failed save: roll back here, or the transaction would keep the
    // database locked until the context is disposed (callers often catch the conflict and carry on).
    public override InterceptionResult ThrowingConcurrencyException(ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        Release(eventData.Context)?.Dispose();
        return result;
    }

    public override async ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Release(eventData.Context) is { } transaction)
        {
            await transaction.DisposeAsync();
        }

        return result;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Release(eventData.Context)?.Dispose();

    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Release(eventData.Context) is { } transaction)
        {
            await transaction.DisposeAsync();
        }
    }

    private void Own(DbContext db, IDbContextTransaction transaction)
    {
        lock (_transactions)
        {
            _transactions[db] = transaction;
        }
    }

    /// <summary>The transaction this guard began for the save (disposing it without a commit rolls it back).</summary>
    private IDbContextTransaction? Release(DbContext? db)
    {
        lock (_transactions)
        {
            return db is not null && _transactions.Remove(db, out var transaction) ? transaction : null;
        }
    }

    private List<AuditRecord> Apply(DbContext db)
    {
        var now = time.GetUtcNow();
        var tenant = user.TenantId;
        var records = new List<AuditRecord>();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Entity is ITenantOwned owned)
            {
                if (entry.State == EntityState.Added && owned.TenantId == Guid.Empty)
                {
                    owned.TenantId = tenant ?? throw new InvalidOperationException($"A new {entry.Metadata.DisplayName()} has no tenant.");
                }

                if (tenant is { } current && owned.TenantId != current)
                {
                    throw new InvalidOperationException($"Refused to write a {entry.Metadata.DisplayName()} of another tenant.");
                }

                if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.TenantId)).IsModified)
                {
                    throw new InvalidOperationException($"The tenant of a {entry.Metadata.DisplayName()} cannot change.");
                }

                if (entry.Entity is not INotAudited && Audit(db, entry, owned.TenantId, now) is { } record)
                {
                    records.Add(record);
                }
            }

            // Removing a soft-deletable entity moves it to the recycle bin; removing it from there purges it.
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable deletable
                && entry.Property(nameof(ISoftDeletable.DeletedAt)).OriginalValue is null)
            {
                entry.State = EntityState.Modified;
                deletable.DeletedAt = now;
                deletable.DeletedBy = user.UserId;
            }

            if (entry.Entity is IVersioned versioned)
            {
                if (entry.State == EntityState.Added)
                {
                    versioned.Version = 1;
                }
                else if (entry.State == EntityState.Modified)
                {
                    versioned.Version++;
                }
            }

            if (entry.Entity is IAuditable auditable)
            {
                // Imports keep the original stamps of what they create (AuditOverrides).
                var stamp = !overrides.IsEmpty && entry.Metadata.FindProperty("Id") is { ClrType: var idType } && idType == typeof(Guid)
                    && overrides.TryGet((Guid)entry.Property("Id").CurrentValue!, out var imported) ? imported : null;
                if (stamp is not null && entry.State is EntityState.Added or EntityState.Modified)
                {
                    if (entry.State == EntityState.Added)
                    {
                        auditable.CreatedAt = stamp.CreatedAt;
                        auditable.CreatedBy = stamp.CreatedBy;
                    }

                    auditable.UpdatedAt = stamp.UpdatedAt ?? stamp.CreatedAt;
                    auditable.UpdatedBy = stamp.UpdatedBy ?? stamp.CreatedBy;
                }
                else if (entry.State == EntityState.Added)
                {
                    auditable.CreatedAt = now;
                    auditable.CreatedBy ??= user.UserId;
                    auditable.UpdatedAt = now;
                    auditable.UpdatedBy = auditable.CreatedBy;
                }
                else if (entry.State == EntityState.Modified)
                {
                    auditable.UpdatedAt = now;
                    auditable.UpdatedBy = user.UserId ?? auditable.UpdatedBy;
                }
            }
        }

        return records;
    }

    private AuditRecord? Audit(DbContext db, EntityEntry entry, Guid tenantId, DateTimeOffset now)
    {
        var softDeletable = entry.Entity is ISoftDeletable;
        var wasDeleted = softDeletable && entry.Property(nameof(ISoftDeletable.DeletedAt)).OriginalValue is not null;
        string? properties = null;
        string action;
        switch (entry.State)
        {
            case EntityState.Added:
                action = AuditActions.Created;
                break;
            case EntityState.Deleted:
                action = softDeletable && !wasDeleted ? AuditActions.Deleted : AuditActions.Purged;
                break;
            case EntityState.Modified when wasDeleted && ((ISoftDeletable)entry.Entity).DeletedAt is null:
                action = AuditActions.Restored;
                break;
            default:
                action = AuditActions.Updated;
                var changed = new List<string>();
                foreach (var property in entry.Properties)
                {
                    if (property.IsModified && !TechnicalProperties.Contains(property.Metadata.Name))
                    {
                        changed.Add(property.Metadata.Name);
                    }
                }

                if (changed.Count == 0)
                {
                    return null;
                }

                properties = string.Join(',', changed);
                break;
        }

        var entityId = entry.Metadata.FindProperty("Id") is { ClrType: var idType } && idType == typeof(Guid) ? (Guid?)entry.Property("Id").CurrentValue : null;
        return new AuditRecord(Ids.New(), tenantId, now.ToUnixTimeMilliseconds(), user.UserId, action,
            $"{ModuleName(db.GetType())}.{entry.Metadata.ClrType.Name}", entityId, properties, Activity.Current?.TraceId.ToHexString());
    }

    /// <summary><c>ListsDbContext</c> → <c>lists</c>.</summary>
    private static string ModuleName(Type contextType) =>
        ModuleNames.GetOrAdd(contextType, t => (t.Name.EndsWith("DbContext", StringComparison.Ordinal) ? t.Name[..^"DbContext".Length] : t.Name).ToLowerInvariant());
}
