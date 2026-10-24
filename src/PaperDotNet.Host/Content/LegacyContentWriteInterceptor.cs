using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using PaperDotNet.Abstractions;
using PaperDotNet.Content;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Data;
using PaperDotNet.Lists.Data;
using PaperDotNet.Persistence;
using PaperDotNet.Taxonomy.Data;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Host.Content;

/// <summary>Serializes migration against module saves and freezes migrated source content, including background writes.</summary>
internal sealed class LegacyContentWriteInterceptor(ContentDatabaseFactory factory, IContentMigrationLock migrationLock, ITenantContext tenant)
    : SaveChangesInterceptor, IModuleDatabaseInterceptor
{
    private readonly Dictionary<DbContext, IDbContextTransaction> owned = [];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) await GuardAsync(context, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context) GuardAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    private async Task GuardAsync(DbContext context, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId || context is not (ListsDbContext or WorkspacesDbContext or DocumentsDbContext or IdentityDbContext or TaxonomyDbContext)
            || !context.ChangeTracker.HasChanges()) return;
        try
        {
            if (context.Database.CurrentTransaction is null) owned[context] = await context.Database.BeginTransactionAsync(ct);
            await migrationLock.AcquireAsync(context, tenantId, ct);
            if (!await factory.ExistsAsync(tenantId, ct)) return;
            await using var target = await factory.OpenAsync(tenantId, ct);
            var migrated = await target.Set<AuditEventsRow>().AsNoTracking().Where(x => x.Action == "content.migrate").Select(x => x.WorkspaceId).Distinct().ToArrayAsync(ct);
            if (migrated.Length == 0) return;
            var ids = migrated.Where(x => x is not null).Select(x => Guid.Parse(x!)).ToArray();
            var changed = context.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
            var blocked = false;
            if (context is ListsDbContext lists)
            {
                var listIds = changed.SelectMany(x => x.Entity switch
                {
                    ListItem item => new[] { item.ListId, x.State == EntityState.Added ? item.ListId : (Guid)x.OriginalValues[nameof(ListItem.ListId)]! },
                    ListView view => [view.ListId],
                    AclEntry acl => [acl.ListId],
                    ItemVersion version => [version.ListId],
                    _ => Array.Empty<Guid>()
                }).Distinct().ToArray();
                var typeIds = changed.Where(x => x.Entity is ContentType).Select(x => ((ContentType)x.Entity).Id).ToArray();
                blocked = changed.Any(x => x.Entity is ListDefinition list && ids.Contains(list.WorkspaceId))
                    || await lists.Lists.IgnoreQueryFilters([QueryFilters.SoftDelete]).AnyAsync(x => ids.Contains(x.WorkspaceId)
                        && (listIds.Contains(x.Id) || x.ContentTypeIds.Any(id => typeIds.Contains(id))), ct);
            }
            else if (context is WorkspacesDbContext)
                blocked = changed.Any(x => x.Entity is Workspace workspace && ids.Contains(workspace.Id) || x.Entity is WorkspaceMember member && ids.Contains(member.WorkspaceId));
            else if (context is DocumentsDbContext)
                blocked = changed.Any(x => x.Entity is FileVersion file && ids.Contains(file.WorkspaceId));
            else if (context is TaxonomyDbContext) blocked = true;
            if (blocked) throw new ContentMigratedException();
        }
        catch { await CompleteAsync(context, false, CancellationToken.None); throw; }
    }

    private async Task CompleteAsync(DbContext? context, bool commit, CancellationToken ct)
    {
        if (context is null || !owned.Remove(context, out var transaction)) return;
        await using (transaction)
        {
            if (commit) await transaction.CommitAsync(ct);
            else await transaction.RollbackAsync(ct);
        }
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    { await CompleteAsync(eventData.Context, true, cancellationToken); return result; }
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    { CompleteAsync(eventData.Context, true, CancellationToken.None).GetAwaiter().GetResult(); return result; }
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default) => CompleteAsync(eventData.Context, false, CancellationToken.None);
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => CompleteAsync(eventData.Context, false, CancellationToken.None).GetAwaiter().GetResult();
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default) => CompleteAsync(eventData.Context, false, CancellationToken.None);
}

internal sealed class ContentMigratedException : InvalidOperationException
{
    public ContentMigratedException() : base("This workspace has moved to Content. Open /content to edit its content and permissions.") { }
}
