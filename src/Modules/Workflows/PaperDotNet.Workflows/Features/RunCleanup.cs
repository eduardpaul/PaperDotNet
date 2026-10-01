using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Options of the workflow module (<c>Workflows</c> section).</summary>
public sealed class WorkflowOptions
{
    public const string Section = "Workflows";

    /// <summary>Finished runs (with their approvals and waits) are deleted after this many days.</summary>
    public int RunRetentionDays { get; set; } = 30;
}

/// <summary>
/// Daily: deletes finished runs older than <see cref="WorkflowOptions.RunRetentionDays"/> with their approvals and
/// bookmarks, and old completions no run ever waited for. Rows are removed in batches (no bulk deletes under Native AOT).
/// </summary>
internal sealed class WorkflowRunCleanupJob(WorkflowsDbContext db, IOptions<WorkflowOptions> options, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "workflows.runCleanup";
    public const string Schedule = "17 3 * * *";
    private const int Batch = 500;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-Math.Max(1, options.Value.RunRetentionDays)).ToUnixTimeMilliseconds();
        db.Bookmarks.RemoveRange(await WaitQueries.UnclaimedAsync(db, tenantId, cutoff, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);

        while (true)
        {
            var runs = await FinishedAsync(db, tenantId, cutoff, cancellationToken);
            if (runs.Count == 0)
            {
                return;
            }

            foreach (var run in runs)
            {
                db.Approvals.RemoveRange(await ApprovalsOfRunAsync(db, tenantId, run.Id, cancellationToken));
                db.Bookmarks.RemoveRange(await BookmarksOfRunAsync(db, tenantId, run.Id, cancellationToken));
            }

            db.WorkflowRuns.RemoveRange(runs);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    private static Task<List<WorkflowRun>> FinishedAsync(WorkflowsDbContext database, Guid tenantId, long cutoffUnixMs, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var cutoff = cutoffUnixMs;
        var completed = RunStatus.Completed;
        var failed = RunStatus.Failed;
        var cancelled = RunStatus.Cancelled;
        var ct = cancellationToken;
        return context.WorkflowRuns
            .Where(r => r.TenantId == tenant && (r.Status == completed || r.Status == failed || r.Status == cancelled) && r.CompletedAtUnixMs != null && r.CompletedAtUnixMs < cutoff)
            .Take(Batch).ToListAsync(ct);
    }

    private static Task<List<ApprovalRequest>> ApprovalsOfRunAsync(WorkflowsDbContext database, Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var run = runId;
        var ct = cancellationToken;
        return context.Approvals.Where(a => a.TenantId == tenant && a.RunId == run).ToListAsync(ct);
    }

    private static Task<List<WorkflowBookmark>> BookmarksOfRunAsync(WorkflowsDbContext database, Guid tenantId, Guid runId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var run = runId;
        var ct = cancellationToken;
        return context.Bookmarks.Where(b => b.TenantId == tenant && b.RunId == run).ToListAsync(ct);
    }
}
