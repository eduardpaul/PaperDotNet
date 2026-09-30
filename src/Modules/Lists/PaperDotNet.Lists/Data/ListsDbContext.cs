using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using PaperDotNet.Lists.Fields;
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
    private readonly FieldTypeRegistry? _fieldTypes;
    private readonly IOptions<Features.ListsOptions>? _listsOptions;

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The model comes from the compiled model generated at publish (ADR-0039).")]
    public ListsDbContext(
        DbContextOptions<ListsDbContext> options, TimeProvider? time = null, FieldTypeRegistry? fieldTypes = null, IOptions<Features.ListsOptions>? listsOptions = null)
        : base(options)
    {
        _time = time ?? TimeProvider.System;
        _fieldTypes = fieldTypes;
        _listsOptions = listsOptions;
    }

    public DbSet<ContentType> ContentTypes { get; set; } = null!;

    public DbSet<ListDefinition> Lists { get; set; } = null!;

    public DbSet<ListItem> Items { get; set; } = null!;

    public DbSet<AclEntry> AclEntries { get; set; } = null!;

    public DbSet<ItemVersion> ItemVersions { get; set; } = null!;

    public DbSet<ListView> Views { get; set; } = null!;

    public DbSet<ItemChange> ItemChanges { get; set; } = null!;

    public DbSet<ItemValue> ItemValues { get; set; } = null!;

    /// <summary>
    /// Saves. A new list gets the permission entries of the workspace roles unless the save brings its own; the change
    /// log for delta sync (API-05) is written from the tracked changes, so every write path is covered.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var newLists = ChangeTracker.Entries<ListDefinition>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        if (newLists.Count > 0)
        {
            var withEntries = ChangeTracker.Entries<AclEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.ScopeId).ToHashSet();
            AclEntries.AddRange(newLists.Where(l => !withEntries.Contains(l.Id)).SelectMany(Features.Acl.RoleEntries));
        }

        await IndexFieldsAsync(cancellationToken);
        RecordChanges(newLists);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Keeps indexed fields current (ADR-0035), whichever path saved: lists that are new or whose content types changed
    /// get their columns planned, and saved items get their columns and value rows written.
    /// </summary>
    private async Task IndexFieldsAsync(CancellationToken cancellationToken)
    {
        if (_fieldTypes is null)
        {
            return; // Design time (migrations).
        }

        await PlanListsAsync(cancellationToken);

        foreach (var purged in ChangeTracker.Entries<ListItem>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity).ToList())
        {
            ItemValues.RemoveRange(await ValuesOfAsync(purged.TenantId, purged.Id, cancellationToken));
        }

        var saved = ChangeTracker.Entries<ListItem>()
            .Where(e => e.State == EntityState.Added || (e.State == EntityState.Modified && e.Property(nameof(ListItem.Fields)).IsModified))
            .ToList();
        var lists = ChangeTracker.Entries<ListDefinition>().Select(e => e.Entity).ToDictionary(l => l.Id);
        foreach (var entry in saved)
        {
            var item = entry.Entity;
            if (!lists.TryGetValue(item.ListId, out var list))
            {
                list = await FindListAsync(item.TenantId, item.ListId, cancellationToken);
                if (list is null)
                {
                    continue;
                }

                lists[list.Id] = list;
            }

            var (columns, values) = Features.FieldIndex.Compute(item.Fields, Features.FieldIndex.Read(list));
            foreach (var (column, value) in columns)
            {
                entry.Property(column).CurrentValue = value;
            }

            var current = entry.State == EntityState.Added ? [] : await ValuesOfAsync(item.TenantId, item.Id, cancellationToken);
            SyncValues(item, current, values);
        }
    }

    /// <summary>Adds and removes <see cref="ItemValue"/> rows so they match the item's values.</summary>
    internal void SyncValues(ListItem item, IReadOnlyCollection<ItemValue> current, Dictionary<short, HashSet<Guid>> values)
    {
        ItemValues.RemoveRange(current.Where(v => !values.TryGetValue(v.Field, out var set) || !set.Contains(v.Value)));
        foreach (var (field, set) in values)
        {
            ItemValues.AddRange(set.Where(value => !current.Any(v => v.Field == field && v.Value == value))
                .Select(value => new ItemValue { TenantId = item.TenantId, ListId = item.ListId, ItemId = item.Id, Field = field, Value = value }));
        }
    }

    internal Task<List<ItemValue>> ValuesOfAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        return ItemValues.Where(v => v.TenantId == tenant && v.ItemId == id).ToListAsync(ct);
    }

    /// <summary>A list of the tenant (tracked), also in the recycle bin.</summary>
    private Task<ListDefinition?> FindListAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken)
    {
        var tenant = tenantId;
        var id = listId;
        var ct = cancellationToken;
        return Lists.Where(l => l.TenantId == tenant && l.Id == id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Plans the indexed fields of new lists, lists whose content types changed, and lists using a changed content type.</summary>
    private async Task PlanListsAsync(CancellationToken cancellationToken)
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

        var types = ChangeTracker.Entries<ContentType>().Select(e => e.Entity).ToDictionary(c => c.Id);
        foreach (var changed in ChangeTracker.Entries<ContentType>().Where(e => e.State == EntityState.Modified).Select(e => e.Entity).ToList())
        {
            foreach (var list in await ListsOfTenantAsync(changed.TenantId, cancellationToken))
            {
                if (Features.ListsJsonText.Ids(list.ContentTypeIds).Contains(changed.Id))
                {
                    plan.TryAdd(list.Id, (list, false));
                }
            }
        }

        if (plan.Count == 0)
        {
            return;
        }

        var limits = _listsOptions?.Value.IndexedFields ?? new Features.IndexedFieldLimits();
        foreach (var (list, isNew) in plan.Values)
        {
            var fields = new List<Contracts.FieldDefinition>();
            foreach (var id in Features.ListsJsonText.Ids(list.ContentTypeIds))
            {
                if (!types.TryGetValue(id, out var type))
                {
                    type = await FindContentTypeAsync(list.TenantId, id, cancellationToken);
                    if (type is null)
                    {
                        continue;
                    }

                    types[id] = type;
                }

                fields.AddRange(Features.ListsJsonText.Fields(type.Fields));
            }

            Features.FieldIndex.Plan(list, fields, _fieldTypes!, limits, isNew);
        }
    }

    private Task<List<ListDefinition>> ListsOfTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = tenantId;
        var ct = cancellationToken;
        return Lists.Where(l => l.TenantId == tenant && l.DeletedAt == null).ToListAsync(ct);
    }

    private Task<ContentType?> FindContentTypeAsync(Guid tenantId, Guid contentTypeId, CancellationToken cancellationToken)
    {
        var tenant = tenantId;
        var id = contentTypeId;
        var ct = cancellationToken;
        return ContentTypes.AsNoTracking().Where(c => c.TenantId == tenant && c.Id == id).FirstOrDefaultAsync(ct);
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

            // Indexed fields (ADR-0035): one partial index per column, so a list pays only for the columns it fills.
            foreach (var column in Features.FieldIndex.Columns)
            {
                if (!column.StartsWith(IndexKinds.Number, StringComparison.Ordinal))
                {
                    item.Property<string>(column).HasMaxLength(column.StartsWith(IndexKinds.Text, StringComparison.Ordinal) ? Features.FieldIndex.MaxTextLength : 40);
                }

                item.HasIndex(nameof(ListItem.TenantId), nameof(ListItem.ListId), column, nameof(ListItem.Id)).HasFilter($"\"{column}\" IS NOT NULL");
            }
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

        modelBuilder.Entity<ItemValue>(value =>
        {
            value.ToTable("item_values");
            value.HasKey(v => new { v.ItemId, v.Field, v.Value });
            value.HasIndex(v => new { v.TenantId, v.ListId, v.Field, v.Value, v.ItemId });
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
