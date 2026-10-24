using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Content;

/// <summary>PaperGo's organization database. Never apply the modular host's migrations to it.</summary>
public abstract partial class ContentDbContext(DbContextOptions options) : DbContext(options)
{
    public const string ReferenceCommit = "485be496d5c3b9077c9a6238cd8f11d3e4bffeda";

    public DbSet<ResourcesRow> Resources => Set<ResourcesRow>();
    public DbSet<SchemaRevisionsRow> SchemaRevisions => Set<SchemaRevisionsRow>();
    public DbSet<ItemRevisionsRow> ItemRevisions => Set<ItemRevisionsRow>();
    public DbSet<ItemHistoriesRow> ItemHistories => Set<ItemHistoriesRow>();
    public DbSet<ItemSurfacesRow> ItemSurfaces => Set<ItemSurfacesRow>();
    public DbSet<PublicationsRow> Publications => Set<PublicationsRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureRows(modelBuilder);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CheckImmutableRows();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        CheckImmutableRows();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Normalizes generated timestamps to the provider's stored precision.</summary>
    public virtual DateTime NormalizeTimestamp(DateTime value) => value;

    /// <summary>Provider lock used by all writers, matching PaperGo's workspace lock.</summary>
    public abstract Task LockWorkspaceAsync(string resourceId, CancellationToken cancellationToken);

    /// <summary>Serializes notification positions across workspaces.</summary>
    public abstract Task LockLivePositionAsync(CancellationToken cancellationToken);

    private void CheckImmutableRows()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is SchemaRevisionsRow or ItemRevisionsRow or PublicationsRow
                && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Content, schema revisions and publications are immutable. Record a new revision instead.");
            }
        }
    }
}
