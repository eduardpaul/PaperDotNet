using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists.Data;

public sealed class ListsDbContext(DbContextOptions<ListsDbContext> options, ITenantContext tenant, TimeProvider? time = null)
    : DbContext(options), ITenantScopedDbContext
{
    private readonly TimeProvider time = time ?? TimeProvider.System;

    public const string Schema = "lists";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<ContentType> ContentTypes => Set<ContentType>();

    public DbSet<ListDefinition> Lists => Set<ListDefinition>();

    public DbSet<ListItem> Items => Set<ListItem>();

    public DbSet<ListView> Views => Set<ListView>();

    public DbSet<ItemVersion> ItemVersions => Set<ItemVersion>();

    public DbSet<PermissionGrant> Grants => Set<PermissionGrant>();

    public DbSet<ItemChange> ItemChanges => Set<ItemChange>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RecordChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RecordChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Writes the change log for delta sync (API-05) from the tracked changes, so every write
    /// path is covered. Permission changes reset the list's delta tokens.
    /// </summary>
    private void RecordChanges()
    {
        var now = time.GetUtcNow();
        var items = new Dictionary<Guid, ItemChange>();
        var resets = new HashSet<Guid>();
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            switch (entry.Entity)
            {
                case ListItem item when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted:
                    var deleted = entry.State == EntityState.Deleted || item.DeletedAt is not null;
                    if (entry.State == EntityState.Modified
                        && (entry.Property(nameof(ListItem.ScopeId)).IsModified || entry.Property(nameof(ListItem.HasUniquePermissions)).IsModified))
                    {
                        resets.Add(item.ListId);
                    }

                    items[item.Id] = new ItemChange
                    {
                        ListId = item.ListId,
                        ItemId = item.Id,
                        ScopeId = item.ScopeId,
                        Kind = deleted ? ItemChangeKind.Deleted : ItemChangeKind.Upserted,
                        At = now,
                    };
                    break;
                case PermissionGrant grant when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted:
                    resets.Add(grant.ListId);
                    break;
                case ListDefinition list when entry.State == EntityState.Modified && entry.Property(nameof(ListDefinition.HasUniquePermissions)).IsModified:
                    resets.Add(list.Id);
                    break;
            }
        }

        ItemChanges.AddRange(items.Values);
        ItemChanges.AddRange(resets.Select(listId => new ItemChange { ListId = listId, Kind = ItemChangeKind.Reset, At = now }));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ContentType>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(200);
            b.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
            b.Property(c => c.Key).HasMaxLength(150);
            b.Property(c => c.ExtensionId).HasMaxLength(100);
            b.HasIndex(c => new { c.TenantId, c.Key }).IsUnique();
            b.ComplexCollection(c => c.Fields, f => f.ToJson());
        });

        modelBuilder.Entity<ListDefinition>(b =>
        {
            b.ToTable("lists");
            b.Property(l => l.Name).HasMaxLength(200);
            b.HasIndex(l => new { l.TenantId, l.WorkspaceId });
            b.Property(l => l.Versioning).HasConversion<string>().HasMaxLength(20);
            b.Property(l => l.SystemKey).HasMaxLength(50);
            b.Property(l => l.TemplateKey).HasMaxLength(150);
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

        modelBuilder.Entity<ItemChange>(b =>
        {
            b.ToTable("item_changes");
            b.HasKey(c => c.Sequence);
            b.Property(c => c.Sequence).ValueGeneratedOnAdd();
            b.HasIndex(c => new { c.ListId, c.Sequence });
            b.HasIndex(c => new { c.TenantId, c.At });
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
