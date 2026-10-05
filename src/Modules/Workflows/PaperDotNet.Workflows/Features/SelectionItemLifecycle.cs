using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

internal sealed class SelectionItemLifecycle(WorkflowsDbContext db, RunService runs) : IEventSubscriber<ItemDeleted>, IEventSubscriber<ItemPurged>
{
    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) => CancelAsync(integrationEvent.ItemId, cancellationToken, integrationEvent.WorkflowRunId);
    public Task HandleAsync(ItemPurged integrationEvent, CancellationToken cancellationToken) => CancelAsync(integrationEvent.ItemId, cancellationToken);
    private async Task CancelAsync(Guid itemId, CancellationToken ct, Guid? originatingRun = null)
    {
        var affected = await db.Runs.Where(r => r.IsSelection && r.Id != originatingRun && (r.Status == RunStatus.Running || r.Status == RunStatus.Waiting)
            && db.RunItems.Any(i => i.RunId == r.Id && i.ItemId == itemId)).ToListAsync(ct);
        foreach (var run in affected) await runs.CancelAsync(run, ct);
    }
}
