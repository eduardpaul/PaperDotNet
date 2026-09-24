using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
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
/// Runs due recurring jobs for every active tenant. A job run is claimed by
/// advancing its next-run time with an optimistic concurrency check, so only
/// one node runs it even when several app instances share the database.
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

        // Start after the host is up, so startup migrations have run.
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

    internal async Task RunIfDueAsync(RecurringJobRegistration job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();
            var state = await db.RecurringJobs.FirstOrDefaultAsync(j => j.Name == job.Name, ct);
            if (state is null)
            {
                db.RecurringJobs.Add(new RecurringJobState { Name = job.Name, NextRunAt = job.Next(now) });
                await TrySaveAsync(db, ct);
                return;
            }

            if (state.NextRunAt > now)
            {
                return;
            }

            state.NextRunAt = job.Next(now);
            state.LastRunAt = now;
            if (!await TrySaveAsync(db, ct))
            {
                return; // Another node claimed this run.
            }
        }

        var failures = await RunForAllTenantsAsync(job, ct);
        await using var resultScope = scopes.CreateAsyncScope();
        var resultDb = resultScope.ServiceProvider.GetRequiredService<JobsDbContext>();
        await resultDb.RecurringJobs.Where(j => j.Name == job.Name).ExecuteUpdateAsync(
            s => s.SetProperty(j => j.LastStatus, failures.Count == 0 ? "Succeeded" : "Failed")
                  .SetProperty(j => j.LastError, failures.Count == 0 ? null : string.Join("; ", failures)),
            ct);
    }

    private async Task<List<string>> RunForAllTenantsAsync(RecurringJobRegistration job, CancellationToken ct)
    {
        IReadOnlyList<TenantSummary> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(ct);
        }

        var failures = new List<string>();
        foreach (var tenant in tenants.Where(t => t.Status == TenantStatus.Active))
        {
            await using var tenantScope = scopes.CreateAsyncScope();
            var tenantScopes = tenantScope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = tenantScopes.CreateScope(tenant.Id, tenant.Identifier);
            try
            {
                await ((ITenantRecurringJob)scope.ServiceProvider.GetRequiredService(job.JobType)).RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

    private static async Task<bool> TrySaveAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - Retention;
        await db.Operations
            .Where(o => (o.Status == OperationStatus.Succeeded || o.Status == OperationStatus.Failed) && o.CompletedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
