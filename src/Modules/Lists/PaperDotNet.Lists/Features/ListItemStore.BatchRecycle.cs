using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;

namespace PaperDotNet.Lists.Features;

internal sealed partial class ListItemStore
{
    public Task RecycleAsync(IReadOnlyList<ItemRecycleTarget> targets, DbTransaction transaction, CancellationToken cancellationToken) =>
        SharedTransaction.RunAsync(db, transaction, async ct =>
        {
            // A previous caller-owned transaction may have rolled back after saving the batch.
            db.ChangeTracker.Clear();
            foreach (var target in targets.OrderBy(i => i.ItemId))
            {
                // Hold each participant, including the retained item, until commit. Tenant and soft-delete filters stay active.
                var claimed = await db.Items.Where(i => i.Id == target.ItemId && i.ListId == target.ListId && i.Version == target.Version)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.Version, i => i.Version), ct);
                if (claimed != 1) throw new InvalidOperationException("An item changed before the batch could be applied.");
                var (schema, item, problem) = await LoadForChangeAsync(target.WorkspaceId, target.ListId, target.ItemId, target.Version, ct);
                if (problem is not null || item!.IsFolder) throw new InvalidOperationException("An item changed or you may no longer change it.");
                if (target.Recycle)
                {
                    var result = await writer.DeleteAsync(schema!, item, ct, announce: false, workflowRunId: target.WorkflowRunId, deferCommit: true);
                    if (result.Item is null) throw new InvalidOperationException("An item could not be recycled; the batch was rolled back.");
                }
            }
        }, cancellationToken);

    public async Task AnnounceAsync(IReadOnlyList<ItemRecycleTarget> targets, CancellationToken cancellationToken)
    {
        await writer.FlushEventsAsync(cancellationToken);
        foreach (var target in targets.Where(i => i.Recycle))
        {
            var schema = await LoadAsync(target.WorkspaceId, target.ListId, cancellationToken);
            var item = await db.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == target.ItemId && i.ListId == target.ListId, cancellationToken);
            if (schema is not null && item is not null) await writer.PublishChangedAsync("deleted", schema, item, cancellationToken);
        }
    }
}
