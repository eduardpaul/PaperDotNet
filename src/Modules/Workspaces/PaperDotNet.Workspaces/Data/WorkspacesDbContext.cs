using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Workspaces.Data;

public sealed class WorkspacesDbContext(DbContextOptions<WorkspacesDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "workspaces";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<WorkspaceMember> Members => Set<WorkspaceMember>();

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
