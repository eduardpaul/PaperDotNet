using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists.Data;

public sealed class ListsDbContext(DbContextOptions<ListsDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "lists";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<ContentType> ContentTypes => Set<ContentType>();

    public DbSet<ListDefinition> Lists => Set<ListDefinition>();

    public DbSet<ListItem> Items => Set<ListItem>();

    public DbSet<ListView> Views => Set<ListView>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ContentType>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(200);
            b.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
            b.ComplexCollection(c => c.Fields, f => f.ToJson());
        });

        modelBuilder.Entity<ListDefinition>(b =>
        {
            b.ToTable("lists");
            b.Property(l => l.Name).HasMaxLength(200);
            b.HasIndex(l => new { l.TenantId, l.WorkspaceId });
        });

        modelBuilder.Entity<ListItem>(b =>
        {
            b.ToTable("items");
            b.Property(i => i.Title).HasMaxLength(1024);
            b.HasIndex(i => new { i.ListId, i.ParentId });
            b.Property(i => i.Fields).IsJsonDocument();
            b.HasIndex(i => i.Fields).IsJsonContainmentIndex();
        });

        modelBuilder.Entity<ListView>(b =>
        {
            b.ToTable("views");
            b.Property(v => v.Name).HasMaxLength(200);
            b.HasIndex(v => v.ListId);
        });

        modelBuilder.ApplyPaperDotNetConventions(this);
    }
}
