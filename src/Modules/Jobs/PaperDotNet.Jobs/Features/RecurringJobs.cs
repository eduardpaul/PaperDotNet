using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Jobs.Data;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Jobs.Features;

internal static class CronSchedules
{
    /// <summary>Next occurrence of a cron schedule (5 fields, or 6 with seconds), in UTC.</summary>
    public static DateTimeOffset Next(this RecurringJobRegistration job, DateTimeOffset after)
    {
        var format = job.Schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        return CronExpression.Parse(job.Schedule, format).GetNextOccurrence(after, TimeZoneInfo.Utc) ?? DateTimeOffset.MaxValue;
    }
}

/// <summary>Configuration section <c>Jobs</c>.</summary>
public sealed class JobsOptions
{
    public const string Section = "Jobs";

    /// <summary>How often the scheduler checks for due jobs.</summary>
    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(30);

    public bool SchedulerEnabled { get; set; } = true;
}

/// <summary>
/// Runs due recurring jobs for every active tenant. A job run is claimed by advancing its next-run time with an
/// optimistic concurrency check, so only one server runs it even when several share the database.
/// </summary>
internal sealed partial class RecurringJobScheduler(
    IServiceScopeFactory scopes,
    IEnumerable<RecurringJobRegistration> jobs,
    IOptions<JobsOptions> options,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<RecurringJobScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.SchedulerEnabled)
        {
            return;
        }

        // Start after the host is up, so the schema is in place.
        var started = new TaskCompletionSource();
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
        {
            try
            {
                await started.Task;
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        using var timer = new PeriodicTimer(options.Value.SchedulerInterval, time);
        do
        {
            foreach (var job in jobs)
            {
                try
                {
                    await RunIfDueAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // One failing job must not stop the scheduler.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogJobFailed(ex, job.Name);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunIfDueAsync(RecurringJobRegistration job, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
            var state = await FindAsync(db, job.Name, cancellationToken);
            if (state is null)
            {
                db.RecurringJobs.Add(new RecurringJobState { Name = job.Name, NextRunAt = job.Next(now) });
                await TrySaveAsync(db, cancellationToken);
                return;
            }

            if (state.NextRunAt > now)
            {
                return;
            }

            state.NextRunAt = job.Next(now);
            state.LastRunAt = now;
            if (!await TrySaveAsync(db, cancellationToken))
            {
                return; // Another server claimed this run.
            }
        }

        var failures = await RunForAllTenantsAsync(job, cancellationToken);
        await using var resultScope = scopes.CreateAsyncScope();
        var resultDb = resultScope.ServiceProvider.GetRequiredService<JobsDbContext>();
        if (await FindAsync(resultDb, job.Name, cancellationToken) is { } result)
        {
            result.LastStatus = failures.Count == 0 ? "succeeded" : "failed";
            result.LastError = failures.Count == 0 ? null : string.Join("; ", failures);
            await TrySaveAsync(resultDb, cancellationToken);
        }
    }

    private static Task<RecurringJobState?> FindAsync(JobsDbContext database, string name, CancellationToken cancellationToken)
    {
        var db = database;
        var jobName = name;
        var ct = cancellationToken;
        return db.RecurringJobs.Where(j => j.Name == jobName).FirstOrDefaultAsync(ct);
    }

    private async Task<List<string>> RunForAllTenantsAsync(RecurringJobRegistration job, CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantSummary> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(cancellationToken);
        }

        var failures = new List<string>();
        foreach (var tenant in tenants.Where(t => t.Status == TenantStatus.Active))
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                await job.Create(scope.ServiceProvider).RunAsync(tenant.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Shutting down: not a tenant failure.
            }
#pragma warning disable CA1031 // A failure in one tenant must not skip the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTenantJobFailed(ex, job.Name, tenant.Identifier);
                failures.Add($"{tenant.Identifier}: {ex.Message}");
            }
        }

        return failures;
    }

    private static async Task<bool> TrySaveAsync(DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job {Job} failed.")]
    private partial void LogJobFailed(Exception exception, string job);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job {Job} failed for tenant {Tenant}.")]
    private partial void LogTenantJobFailed(Exception exception, string job, string tenant);
}

/// <summary>Deletes finished operations older than 30 days (daily at 03:00 UTC).</summary>
internal sealed class OperationsCleanupJob(JobsDbContext db, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "jobs.operations-cleanup";
    public const string Schedule = "0 3 * * *";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var context = db;
        var tenant = tenantId;
        var succeeded = OperationStatus.Succeeded;
        var failed = OperationStatus.Failed;
        var ct = cancellationToken;
        // SQLite cannot compare DateTimeOffset values in SQL: the (few) finished operations are filtered here.
        var finished = await context.Operations
            .Where(o => o.TenantId == tenant && (o.Status == succeeded || o.Status == failed))
            .Select(o => new OperationCompletion(o.Id, o.CompletedAt))
            .ToListAsync(ct);
        var cutoff = time.GetUtcNow() - Retention;
        var expired = finished.Where(o => o.CompletedAt < cutoff).ToList();
        if (expired.Count == 0)
        {
            return;
        }

        context.Operations.RemoveRange(expired.Select(o => new Operation { Id = o.Id, TenantId = tenant }));
        await context.SaveChangesAsync(ct);
    }
}
