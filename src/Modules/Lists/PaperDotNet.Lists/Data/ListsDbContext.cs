using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Features;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists.Data;

public sealed class ListsDbContext(
    DbContextOptions<ListsDbContext> options, ITenantContext tenant, TimeProvider? time = null, FieldTypeRegistry? fieldTypes = null,
    IOptions<ListsOptions>? listsOptions = null)
    : DbContext(options), ITenantScopedDbContext
{
    private readonly TimeProvider time = time ?? TimeProvider.System;

    public const string Schema = "lists";

    public Guid? CurrentTenantId => tenant.TenantId;

    public DbSet<ContentType> ContentTypes => Set<ContentType>();

    public DbSet<ListDefinition> Lists => Set<ListDefinition>();

    public DbSet<ListItem> Items => Set<ListItem>();

    public DbSet<ItemRelation> Relations => Set<ItemRelation>();

    public DbSet<ItemRelationshipType> RelationshipTypes => Set<ItemRelationshipType>();

    public DbSet<ListView> Views => Set<ListView>();

    public DbSet<ItemVersion> ItemVersions => Set<ItemVersion>();

    public DbSet<AclEntry> AclEntries => Set<AclEntry>();

    public DbSet<ItemChange> ItemChanges => Set<ItemChange>();

    public DbSet<SmartFolder> SmartFolders => Set<SmartFolder>();

    public DbSet<ItemValue> ItemValues => Set<ItemValue>();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await IndexFieldsAsync(cancellationToken);
        RecordChanges();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        IndexFieldsAsync(CancellationToken.None).GetAwaiter().GetResult();
        RecordChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Keeps indexed fields current (ADR-0035), whichever path saved: lists that are new or whose content types
    /// changed get their columns planned, and saved items get their columns and value rows written.
    /// </summary>
    private async Task IndexFieldsAsync(CancellationToken ct)
    {
        if (fieldTypes is null)
        {
            return; // Design time (migrations).
        }

        await PlanListsAsync(ct);

        var deleted = ChangeTracker.Entries<ListItem>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id).ToArray();
        if (deleted.Length > 0)
        {
            ItemValues.RemoveRange(await ItemValues.Where(v => EF.Parameter(deleted).Contains(v.ItemId)).ToListAsync(ct));
        }

        var saved = ChangeTracker.Entries<ListItem>()
            .Where(e => e.State == EntityState.Added || (e.State == EntityState.Modified
                && (e.Property(nameof(ListItem.Fields)).IsModified || e.Property(nameof(ListItem.ListId)).IsModified)))
            .ToList();
        if (saved.Count == 0)
        {
            return;
        }

        var lists = ChangeTracker.Entries<ListDefinition>().Select(e => e.Entity).ToDictionary(l => l.Id);
        var missing = saved.Select(e => e.Entity.ListId).Distinct().Where(id => !lists.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            foreach (var list in await Lists.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking()
                         .Where(l => EF.Parameter(missing).Contains(l.Id)).ToListAsync(ct))
            {
                lists[list.Id] = list;
            }
        }

        var desired = new Dictionary<Guid, (ListItem Item, Dictionary<short, HashSet<Guid>> Values)>();
        foreach (var entry in saved)
        {
            if (entry.State == EntityState.Modified && entry.Property(nameof(ListItem.ListId)).IsModified)
            {
                // Slot assignments belong to a list; clear the source's slots before indexing the destination.
                foreach (var kind in new[] { IndexKind.Text, IndexKind.Number, IndexKind.Date })
                {
                    for (var n = 1; n <= FieldIndex.SlotsPerKind; n++)
                    {
                        entry.Property(FieldIndex.ColumnName(kind, n)).CurrentValue = null;
                    }
                }
            }

            if (lists.GetValueOrDefault(entry.Entity.ListId) is { } list)
            {
                desired[entry.Entity.Id] = (entry.Entity, FieldIndex.Apply(entry.Entity, list.IndexedFields));
            }
        }

        await SyncValuesAsync(saved.Where(e => e.State == EntityState.Modified).Select(e => e.Entity).ToList(), desired, lists, ct);
    }

    /// <summary>Adds and removes <see cref="ItemValue"/> rows so they match the items' values.</summary>
    internal async Task SyncValuesAsync(
        IReadOnlyCollection<ListItem> existingItems, Dictionary<Guid, (ListItem Item, Dictionary<short, HashSet<Guid>> Values)> desired,
        IReadOnlyDictionary<Guid, ListDefinition> lists, CancellationToken ct)
    {
        var withValues = existingItems.Where(i => lists.GetValueOrDefault(i.ListId)?.IndexedFields.Any(f => f.Kind == IndexKind.Values) == true
                || Entry(i).Property(nameof(ListItem.ListId)).IsModified)
            .Select(i => i.Id).ToArray();
        var existing = (withValues.Length == 0 ? [] : await ItemValues.Where(v => EF.Parameter(withValues).Contains(v.ItemId)).ToListAsync(ct))
            .Where(v => Entry(v).State != EntityState.Deleted)
            .ToLookup(v => v.ItemId);
        foreach (var (itemId, (item, values)) in desired)
        {
            var current = existing[itemId].ToList();
            foreach (var row in current)
            {
                row.ListId = item.ListId;
            }

            ItemValues.RemoveRange(current.Where(v => !values.TryGetValue(v.Field, out var set) || !set.Contains(v.Value)));
            foreach (var (field, set) in values)
            {
                ItemValues.AddRange(set.Where(value => !current.Any(v => v.Field == field && v.Value == value))
                    .Select(value => new ItemValue { ItemId = itemId, Field = field, Value = value, ListId = item.ListId }));
            }
        }
    }

    /// <summary>Plans the indexed fields of new lists, lists whose content types changed, and lists using a changed content type.</summary>
    private async Task PlanListsAsync(CancellationToken ct)
    {
        var plan = new Dictionary<Guid, (ListDefinition List, bool IsNew)>();
        foreach (var entry in ChangeTracker.Entries<ListDefinition>())
        {
            if (entry.State == EntityState.Added)
            {
                plan[entry.Entity.Id] = (entry.Entity, true);
            }
            else if (entry.State == EntityState.Modified && entry.Property(nameof(ListDefinition.ContentTypeIds)).IsModified)
            {
                plan[entry.Entity.Id] = (entry.Entity, false);
            }
        }

        var changedTypes = ChangeTracker.Entries<ContentType>().Where(e => e.State == EntityState.Modified).Select(e => e.Entity.Id).ToHashSet();
        if (changedTypes.Count > 0)
        {
            foreach (var list in await Lists.ToListAsync(ct))
            {
                if (list.ContentTypeIds.Any(changedTypes.Contains))
                {
                    plan.TryAdd(list.Id, (list, false));
                }
            }
        }

        if (plan.Count == 0)
        {
            return;
        }

        var types = ChangeTracker.Entries<ContentType>().Select(e => e.Entity).ToDictionary(c => c.Id);
        var missing = plan.Values.SelectMany(p => p.List.ContentTypeIds).Distinct().Where(id => !types.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            foreach (var type in await ContentTypes.AsNoTracking().Where(c => EF.Parameter(missing).Contains(c.Id)).ToListAsync(ct))
            {
                types[type.Id] = type;
            }
        }

        var limits = listsOptions?.Value.IndexedFields ?? new IndexedFieldLimits();
        foreach (var (list, isNew) in plan.Values)
        {
            var fields = list.ContentTypeIds.Select(types.GetValueOrDefault).OfType<ContentType>().SelectMany(c => c.Fields);
            FieldIndex.Plan(list, fields, fieldTypes!.Find, limits, isNew);
        }
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
                    var location = entry.Property(nameof(ListItem.ListId));
                    if (entry.State == EntityState.Modified && location.IsModified)
                    {
                        ItemChanges.Add(new ItemChange
                        {
                            ListId = (Guid)location.OriginalValue!,
                            ItemId = item.Id,
                            ScopeId = (Guid)scope.OriginalValue!,
                            Kind = ItemChangeKind.Deleted,
                            At = now,
                        });
                    }
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

        modelBuilder.Entity<ItemRelation>(b =>
        {
            b.ToTable("item_relations");
            b.Property(r => r.Attributes).IsJsonDocument();
            b.HasIndex(r => r.Attributes).IsJsonContainmentIndex();
            b.HasIndex(r => new { r.TenantId, r.FirstItemId, r.SecondItemId, r.TypeId, r.Directed }).IsUnique();
            b.HasIndex(r => new { r.TenantId, r.SecondItemId });
            b.HasOne<ListItem>().WithMany().HasForeignKey(r => r.FirstItemId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ListItem>().WithMany().HasForeignKey(r => r.SecondItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ItemRelationshipType>(b =>
        {
            b.ToTable("item_relationship_types");
            b.Property(t => t.InverseLabel).HasMaxLength(256);
        });

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
            b.ComplexCollection(l => l.IndexedFields, f => f.ToJson());
            b.HasIndex(l => new { l.TenantId, l.IndexPending });
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

            // Indexed fields (ADR-0035): one partial index per column, so a list pays only for the columns it fills.
            foreach (var kind in new[] { IndexKind.Text, IndexKind.Number, IndexKind.Date })
            {
                for (var n = 1; n <= FieldIndex.SlotsPerKind; n++)
                {
                    var column = FieldIndex.ColumnName(kind, n);
                    if (kind != IndexKind.Number)
                    {
                        b.Property<string>(column).HasMaxLength(kind == IndexKind.Text ? FieldIndex.MaxTextLength : 40);
                    }

                    b.HasIndex(nameof(ListItem.ListId), column, nameof(ListItem.Id)).HasFilter($"{column.ToLowerInvariant()} IS NOT NULL");
                }
            }

            // Browsing a folder: folders first, then by title (issue 0001).
            b.HasIndex(i => new { i.ListId, i.ParentId, i.IsFolder, i.Title, i.Id });
        });

        modelBuilder.Entity<ItemValue>(b =>
        {
            b.ToTable("item_values");
            b.HasKey(v => new { v.ItemId, v.Field, v.Value });
            b.HasIndex(v => new { v.ListId, v.Field, v.Value, v.ItemId });
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
