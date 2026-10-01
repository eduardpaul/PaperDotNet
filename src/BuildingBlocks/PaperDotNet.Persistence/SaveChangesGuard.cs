using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence;

/// <summary>
/// Stamps versions and audit fields, turns deletes of <see cref="ISoftDeletable"/> entities into soft deletes, and
/// guards tenant isolation on every save. Reads cannot be guarded here (no global
/// query filters under Native AOT, ADR-0039), so every query filters on the tenant itself; writes are checked here:
/// an added row gets the caller's tenant, and a row of another tenant can never be written in a request.
/// </summary>
public sealed class SaveChangesGuard(ICurrentUser user, TimeProvider time, AuditOverrides overrides) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? db)
    {
        if (db is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var tenant = user.TenantId;
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
    }
}
