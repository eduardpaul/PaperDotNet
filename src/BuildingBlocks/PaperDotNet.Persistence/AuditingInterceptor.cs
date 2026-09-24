using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence;

/// <summary>
/// Stamps the tenant and audit columns on save, turns deletes of
/// <see cref="ISoftDeletable"/> into soft deletes, and rejects any write that
/// would touch another tenant's data.
/// </summary>
public sealed class AuditingInterceptor(ITenantContext tenant, ICurrentUser user, TimeProvider time) : SaveChangesInterceptor
{
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
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            if (entry.Entity is ITenantOwned owned)
            {
                EnsureTenant(entry.State, owned);
            }

            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable deletable)
            {
                entry.State = EntityState.Modified;
                deletable.DeletedAt = now;
                deletable.DeletedBy = user.UserId;
            }

            if (entry.Entity is IAuditable audited)
            {
                if (entry.State == EntityState.Added)
                {
                    audited.CreatedAt = now;
                    audited.CreatedBy = user.UserId;
                }

                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    audited.UpdatedAt = now;
                    audited.UpdatedBy = user.UserId;
                }
            }
        }
    }

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
