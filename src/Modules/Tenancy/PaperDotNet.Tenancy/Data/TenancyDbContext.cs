using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Tenancy.Data;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "tenancy";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Tenant>(b =>
        {
            b.Property(t => t.Identifier).HasMaxLength(63);
            b.HasIndex(t => t.Identifier).IsUnique();
            b.Property(t => t.Name).HasMaxLength(200);
        });
        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
