using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Features;
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

    public DbSet<AclEntry> AclEntries => Set<AclEntry>();

    public DbSet<ItemChange> ItemChanges => Set<ItemChange>();

    public DbSet<SmartFolder> SmartFolders => Set<SmartFolder>();

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
    /// Writes the change log for delta sync (API-05) from the tracked changes, so every write path is covered. An item
    /// that moved to another permission scope records the scope it came from; a changed access list records a
    /// <see cref="ItemChangeKind.ScopeChanged"/> marker (ADR-0035). New lists get the entries of the workspace roles,
    /// whichever path created them.
    /// </summary>
    private void RecordChanges()
    {
        var now = time.GetUtcNow();
        var newLists = ChangeTracker.Entries<ListDefinition>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (newLists.Count > 0)
        {
            var withEntries = ChangeTracker.Entries<AclEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.ScopeId).ToHashSet();
            AclEntries.AddRange(newLists.Where(l => !withEntries.Contains(l.Id)).SelectMany(Acl.RoleEntries));
        }

        var items = new Dictionary<Guid, ItemChange>();

        // Scopes created or removed in this save: their items are logged one by one as they move.
        var movingScopes = new HashSet<Guid>(newLists.Select(l => l.Id));
        var scopes = new Dictionary<Guid, Guid>();
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            switch (entry.Entity)
            {
                case ListItem item when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted:
                    var deleted = entry.State == EntityState.Deleted || item.DeletedAt is not null;
                    var scope = entry.Property(nameof(ListItem.ScopeId));
                    if (entry.State == EntityState.Added || entry.Property(nameof(ListItem.HasUniquePermissions)).IsModified)
                    {
                        movingScopes.Add(item.Id);
                    }

                    items[item.Id] = new ItemChange
                    {
                        ListId = item.ListId,
                        ItemId = item.Id,
                        ScopeId = item.ScopeId,
                        FromScopeId = entry.State == EntityState.Modified && scope.IsModified ? (Guid)scope.OriginalValue! : null,
                        Kind = deleted ? ItemChangeKind.Deleted : ItemChangeKind.Upserted,
                        At = now,
                    };
                    break;
                case AclEntry acl when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted:
                    scopes[acl.ScopeId] = acl.ListId;
                    break;
            }
        }

        ItemChanges.AddRange(items.Values);
        ItemChanges.AddRange(scopes.Where(s => !movingScopes.Contains(s.Key)).Select(s => new ItemChange
        {
            ListId = s.Value,
            ScopeId = s.Key,
            Kind = ItemChangeKind.ScopeChanged,
            At = now,
        }));
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

        modelBuilder.Entity<AclEntry>(b =>
        {
            b.ToTable("acl_entries");
            b.HasKey(e => new { e.ScopeId, e.PrincipalId });
            b.Property(e => e.PrincipalType).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.Level).HasConversion<int>();

            // The allowed scopes of a caller come from this index alone (index-only on PostgreSQL).
            b.HasIndex(e => new { e.PrincipalId, e.ListId, e.ScopeId, e.Level, e.TenantId });
            b.HasIndex(e => e.ListId);
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

        modelBuilder.Entity<SmartFolder>(b =>
        {
            b.ToTable("smart_folders");
            b.Property(f => f.Name).HasMaxLength(200);
            b.Property(f => f.Description).HasMaxLength(2000);
            b.HasIndex(f => new { f.TenantId, f.OwnerId });
            b.HasIndex(f => new { f.TenantId, f.WorkspaceId });
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
