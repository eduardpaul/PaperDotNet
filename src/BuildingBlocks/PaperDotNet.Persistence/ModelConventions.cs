using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence;

public static class ModelConventions
{
    /// <summary>
    /// Applies PaperDotNet conventions to every entity in the model:
    /// the named <c>Tenant</c> filter for <see cref="ITenantOwned"/>, the named
    /// <c>SoftDelete</c> filter for <see cref="ISoftDeletable"/>, a tenant index,
    /// and concurrency tokens for <see cref="IVersioned"/>.
    /// Call at the end of <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder ApplyPaperDotNetConventions<TContext>(this ModelBuilder modelBuilder, TContext context)
        where TContext : DbContext, ITenantScopedDbContext
    {
        var contextExpression = Expression.Constant(context);
        var currentTenant = Expression.Property(contextExpression, nameof(ITenantScopedDbContext.CurrentTenantId));

        // Only touch PaperDotNet entities: configuring other types (e.g. Identity's
        // owned passkey data) would turn them into regular entity types.
        var entityTypes = modelBuilder.Model.GetEntityTypes()
            .Where(t => t.BaseType is null && !t.IsOwned())
            .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)
                        || typeof(ISoftDeletable).IsAssignableFrom(t.ClrType)
                        || typeof(IVersioned).IsAssignableFrom(t.ClrType))
            .ToList();
        foreach (var entityType in entityTypes)
        {
            var clrType = entityType.ClrType;
            var entity = modelBuilder.Entity(clrType);
            var parameter = Expression.Parameter(clrType, "e");

            if (typeof(ITenantOwned).IsAssignableFrom(clrType))
            {
                var tenantId = Expression.Convert(Expression.Property(parameter, nameof(ITenantOwned.TenantId)), typeof(Guid?));
                entity.HasQueryFilter(QueryFilters.Tenant, Expression.Lambda(Expression.Equal(tenantId, currentTenant), parameter));
                entity.HasIndex(nameof(ITenantOwned.TenantId));
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                var deletedAt = Expression.Property(parameter, nameof(ISoftDeletable.DeletedAt));
                entity.HasQueryFilter(QueryFilters.SoftDelete, Expression.Lambda(Expression.Equal(deletedAt, Expression.Constant(null, typeof(DateTimeOffset?))), parameter));
            }

            if (typeof(IVersioned).IsAssignableFrom(clrType))
            {
                entity.Property(nameof(IVersioned.Version)).IsConcurrencyToken();
            }
        }

        return modelBuilder;
    }
}
