using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Persistence.Sqlite;
using Wolverine.EntityFrameworkCore;

namespace PaperDotNet.Lists.Data;

/// <summary>
/// Content types, lists, items and permission entries. At run time the compiled model and the precompiled queries
/// generated at publish are used (ADR-0039). Query rules: one LINQ expression from a DbSet property to the terminal
/// operator; the DbContext and every captured value copied into locals first (dotnet/efcore#35887); explicit
/// <c>TenantId</c> and <c>DeletedAt</c> filters (no global query filters under AOT). Dynamic item queries go through
/// <see cref="Querying.IItemQueries"/>.
/// </summary>
public class ListsDbContext : DbContext
{
    private readonly TimeProvider _time;

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public ListsDbContext(DbContextOptions<ListsDbContext> options, TimeProvider? time = null)
        : base(options)
    {
        _time = time ?? TimeProvider.System;
    }

    public DbSet<ContentType> ContentTypes { get; set; } = null!;

    public DbSet<ListDefinition> Lists { get; set; } = null!;

    public DbSet<ListItem> Items { get; set; } = null!;

    public DbSet<AclEntry> AclEntries { get; set; } = null!;

    public DbSet<ItemVersion> ItemVersions { get; set; } = null!;

    public DbSet<ListView> Views { get; set; } = null!;

    public DbSet<ItemChange> ItemChanges { get; set; } = null!;

    /// <summary>
    /// Saves. A new list gets the permission entries of the workspace roles unless the save brings its own; the change
    /// log for delta sync (API-05) is written from the tracked changes, so every write path is covered.
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var newLists = ChangeTracker.Entries<ListDefinition>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (newLists.Count > 0)
        {
            var withEntries = ChangeTracker.Entries<AclEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.ScopeId).ToHashSet();
            AclEntries.AddRange(newLists.Where(l => !withEntries.Contains(l.Id)).SelectMany(Features.Acl.RoleEntries));
        }

        RecordChanges(newLists);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// An item that moved to another permission scope records the scope it came from; a changed access list records a
    /// <see cref="ItemChangeKinds.ScopeChanged"/> marker, unless the scope is new or its items are logged one by one
    /// (ADR-0035). Removing an item (the recycle bin, or a purge) logs it as deleted.
    /// </summary>
    private void RecordChanges(List<ListDefinition> newLists)
    {
        var at = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var items = new Dictionary<Guid, ItemChange>();
        var movingScopes = new HashSet<Guid>(newLists.Select(l => l.Id));
        var scopes = new Dictionary<Guid, (Guid TenantId, Guid ListId)>();
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            switch (entry.Entity)
            {
                case ListItem item when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted:
                    var scope = entry.Property(nameof(ListItem.ScopeId));
                    if (entry.State == EntityState.Added || entry.Property(nameof(ListItem.HasUniquePermissions)).IsModified)
                    {
                        movingScopes.Add(item.Id);
                    }

                    items[item.Id] = new ItemChange
                    {
                        TenantId = item.TenantId,
                        ListId = item.ListId,
                        ItemId = item.Id,
                        ScopeId = item.ScopeId,
                        FromScopeId = entry.State == EntityState.Modified && scope.IsModified ? (Guid)scope.OriginalValue! : null,
                        Kind = entry.State == EntityState.Deleted || item.DeletedAt is not null ? ItemChangeKinds.Deleted : ItemChangeKinds.Upserted,
                        At = at,
                    };
                    break;
                case AclEntry acl when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted:
                    scopes[acl.ScopeId] = (acl.TenantId, acl.ListId);
                    break;
            }
        }

        ItemChanges.AddRange(items.Values);
        ItemChanges.AddRange(scopes.Where(s => !movingScopes.Contains(s.Key)).Select(s => new ItemChange
        {
            TenantId = s.Value.TenantId,
            ListId = s.Value.ListId,
            ScopeId = s.Key,
            Kind = ItemChangeKinds.ScopeChanged,
            At = at,
        }));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Wolverine's inbox/outbox tables, written in the same transaction; Wolverine creates them itself.
        modelBuilder.MapWolverineEnvelopeStorage();

        modelBuilder.Entity<ContentType>(contentType =>
        {
            contentType.ToTable("content_types");
            contentType.Property(c => c.Name).HasMaxLength(200);
            contentType.Property(c => c.Key).HasMaxLength(150);
            contentType.Property(c => c.ExtensionId).HasMaxLength(100);
            contentType.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
            contentType.HasIndex(c => new { c.TenantId, c.Key });
        });

        modelBuilder.Entity<ListDefinition>(list =>
        {
            list.ToTable("lists");
            list.Property(l => l.Name).HasMaxLength(200);
            list.Property(l => l.Description).HasMaxLength(2000);
            list.Property(l => l.Kind).HasMaxLength(16);
            list.Property(l => l.Versioning).HasMaxLength(16);
            list.Property(l => l.TemplateKey).HasMaxLength(150);
            list.Property(l => l.SystemKey).HasMaxLength(64);
            list.HasIndex(l => new { l.TenantId, l.WorkspaceId });
        });

        modelBuilder.Entity<ListItem>(item =>
        {
            item.ToTable("list_items");
            item.Property(i => i.Title).HasMaxLength(1024);
            item.HasIndex(i => new { i.TenantId, i.ListId, i.Id });
            item.HasIndex(i => new { i.TenantId, i.ListId, i.ParentId, i.IsFolder, i.Title });
            item.HasIndex(i => new { i.TenantId, i.ScopeId });
            item.HasOne<ListDefinition>().WithMany().HasForeignKey(i => i.ListId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AclEntry>(entry =>
        {
            entry.ToTable("acl_entries");
            entry.HasKey(e => new { e.ScopeId, e.PrincipalId });
            entry.Property(e => e.PrincipalType).HasMaxLength(20);
            entry.HasIndex(e => new { e.TenantId, e.PrincipalId, e.ListId });
            entry.HasIndex(e => new { e.TenantId, e.ListId });
        });

        modelBuilder.Entity<ItemVersion>(version =>
        {
            version.ToTable("item_versions");
            version.Property(v => v.Title).HasMaxLength(1024);
            version.HasIndex(v => new { v.TenantId, v.ItemId, v.Number }).IsUnique();
        });

        modelBuilder.Entity<ItemChange>(change =>
        {
            change.ToTable("item_changes");
            change.HasKey(c => c.Sequence);
            change.Property(c => c.Sequence).ValueGeneratedOnAdd();
            change.Property(c => c.Kind).HasMaxLength(16);
            change.HasIndex(c => new { c.TenantId, c.ListId, c.Sequence });
            change.HasIndex(c => new { c.TenantId, c.At });
        });

        modelBuilder.Entity<ListView>(view =>
        {
            view.ToTable("list_views");
            view.Property(v => v.Name).HasMaxLength(200);
            view.Property(v => v.Filter).HasMaxLength(4000);
            view.Property(v => v.OrderBy).HasMaxLength(1000);
            view.Property(v => v.GroupBy).HasMaxLength(100);
            view.Property(v => v.Layout).HasMaxLength(16);
            view.HasIndex(v => new { v.TenantId, v.ListId });
            view.HasOne<ListDefinition>().WithMany().HasForeignKey(v => v.ListId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>For the EF Core tools: the compiled model, precompiled queries and migrations.</summary>
internal sealed class ListsDesignTimeFactory : IDesignTimeDbContextFactory<ListsDbContext>
{
    public ListsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<ListsDbContext>());
}
