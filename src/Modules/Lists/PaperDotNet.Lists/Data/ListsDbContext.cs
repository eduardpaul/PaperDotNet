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

    public DbSet<ItemVersion> ItemVersions => Set<ItemVersion>();

    public DbSet<PermissionGrant> Grants => Set<PermissionGrant>();

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
            b.Property(l => l.Versioning).HasConversion<string>().HasMaxLength(20);
            b.Property(l => l.SystemKey).HasMaxLength(50);
            b.HasIndex(l => new { l.WorkspaceId, l.SystemKey }).IsUnique();
        });

        modelBuilder.Entity<PermissionGrant>(b =>
        {
            b.ToTable("permission_grants");
            b.Property(g => g.PrincipalType).HasConversion<string>().HasMaxLength(10);
            b.Property(g => g.Level).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(g => new { g.ObjectId, g.PrincipalType, g.PrincipalId }).IsUnique();
            b.HasIndex(g => g.ListId);
        });

        modelBuilder.Entity<ItemVersion>(b =>
        {
            b.ToTable("item_versions");
            b.Property(v => v.Title).HasMaxLength(1024);
            b.HasIndex(v => new { v.ItemId, v.Number }).IsUnique();
        });

        modelBuilder.Entity<ListItem>(b =>
        {
            b.ToTable("items");
            b.Property(i => i.Title).HasMaxLength(1024);
            b.HasIndex(i => new { i.ListId, i.ParentId });
            b.HasIndex(i => new { i.ListId, i.ScopeId });
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
