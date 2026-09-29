using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Workspaces.Data;

public sealed class WorkspacesDbContext(DbContextOptions<WorkspacesDbContext> options, ITenantContext tenant, HybridCache? cache = null)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "workspaces";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<WorkspaceMember> Members => Set<WorkspaceMember>();

    /// <summary>
    /// Saves, and evicts the cached principals of the tenant when memberships changed or a workspace was added
    /// (administrators own every workspace), so access checks see the change at once (ADR-0035).
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var membershipsChanged = ChangeTracker.Entries().Any(e =>
            (e.Entity is WorkspaceMember && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || (e.Entity is Workspace && e.State == EntityState.Added));
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (membershipsChanged && cache is not null && tenant.TenantId is { } tenantId)
        {
            await cache.RemoveByTagAsync(AccessCacheTags.Principals(tenantId), cancellationToken);
        }

        return saved;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Workspace>(b =>
        {
            b.Property(w => w.Name).HasMaxLength(200);
            b.Property(w => w.Description).HasMaxLength(2000);
            b.HasMany(w => w.Members).WithOne().HasForeignKey(m => m.WorkspaceId);
            b.HasIndex(w => new { w.TenantId, w.PersonalOwnerId }).IsUnique();
        });
        modelBuilder.Entity<WorkspaceMember>(b =>
        {
            b.HasKey(m => new { m.WorkspaceId, m.UserId });
            b.HasIndex(m => m.UserId);
        });
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
