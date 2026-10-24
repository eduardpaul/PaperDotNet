using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Content;

namespace PaperDotNet.Host.Content;

internal sealed class ContentMigrationState(ContentDatabaseFactory factory, ITenantContext tenant) : IContentMigrationState
{
    public async Task<IReadOnlyCollection<Guid>> GetMigratedWorkspacesAsync(CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } id || !await factory.ExistsAsync(id, cancellationToken)) return [];
        await using var db = await factory.OpenAsync(id, cancellationToken);
        var migrated = await db.Set<AuditEventsRow>().AsNoTracking().Where(x => x.Action == "content.migrate" && x.WorkspaceId != null)
            .Select(x => x.WorkspaceId).Distinct().ToArrayAsync(cancellationToken);
        return migrated.Select(x => Guid.Parse(x!)).ToArray();
    }
}
