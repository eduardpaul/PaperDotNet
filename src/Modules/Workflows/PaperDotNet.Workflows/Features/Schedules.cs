using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Cronos;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Cron expressions of schedule triggers and stable event ids for timed starts.</summary>
internal static class TriggerSchedules
{
    /// <summary>A 5-field cron expression, or null when it is not one.</summary>
    public static CronExpression? ParseCron(string? cron) =>
        cron is { Length: > 0 } && cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 5
            && CronExpression.TryParse(cron, CronFormat.Standard, out var expression)
            ? expression
            : null;

    /// <summary>
    /// The event id of a timed start (an occurrence, or an item's date): the same inputs give the same id, so the unique
    /// run per workflow and event makes a second start of the same occurrence impossible.
    /// </summary>
    public static Guid EventId(params object[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)))));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

/// <summary>
/// Every minute, starts the workflows of timed triggers (EVT-10, EVT-11, ADR-0036):
/// <list type="bullet">
/// <item><c>schedule</c>: one run per occurrence of the cron expression, in the trigger's time zone or the
/// organization's. Occurrences missed while the server was down run once.</item>
/// <item><c>date</c>: one run per item and date value when the item's date field plus <c>offsetHours</c> is reached
/// (dates without a time count from midnight in the organization's time zone). Only moments after the workflow was
/// saved or turned on count, so a new workflow does not run for dates in the past.</item>
/// </list>
/// The state per timed trigger is a <see cref="WorkflowSchedule"/> row (<see cref="StateId"/>); a new version or turning the
/// workflow off starts over.
/// </summary>
internal sealed class WorkflowScheduleJob(
    WorkflowsDbContext db, WorkflowStarter starter, IListItemStore items, IUserPreferences preferences, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "workflows.schedules";
    public const string Schedule = "* * * * *";

    /// <summary>Items read per page of a date trigger's query (all due items start in the same minute).</summary>
    private const int PageSize = 500;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var workflows = (await db.Workflows.AsNoTracking().Where(TriggerColumn.Has(WorkflowTriggers.Schedule)).ToListAsync(cancellationToken))
            .Concat(await db.Workflows.AsNoTracking().Where(TriggerColumn.Has(WorkflowTriggers.Date)).ToListAsync(cancellationToken))
            .DistinctBy(w => w.Id)
            .ToList();
        var states = await db.Schedules.ToDictionaryAsync(s => s.Id, cancellationToken);

        // The timed triggers of the enabled workflows.
        var timed = new List<(WorkflowDefinition Workflow, WorkflowSpec Spec, WorkflowTrigger Trigger, int Index)>();
        foreach (var workflow in workflows.Where(w => w.Enabled))
        {
            var version = await db.Versions.AsNoTracking().FirstAsync(v => v.WorkflowId == workflow.Id && v.Number == workflow.CurrentVersion, cancellationToken);
            var spec = DefinitionJson.Deserialize<WorkflowSpec>(version.Definition);
            timed.AddRange(spec.AllTriggers.Select((t, i) => (workflow, spec, t, i)).Where(t => t.t.Type is WorkflowTriggers.Schedule or WorkflowTriggers.Date));
        }

        // Triggers of workflows that were turned off, deleted or changed start over when they come back.
        var active = timed.Select(t => StateId(t.Workflow.Id, t.Index)).ToHashSet();
        db.Schedules.RemoveRange(states.Values.Where(s => !active.Contains(s.Id)));

        TimeZoneInfo? organizationZone = null;
        async Task<TimeZoneInfo> ZoneAsync(string? zone) =>
            zone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var found)
                ? found
                : organizationZone ??= (await preferences.GetDefaultsAsync(cancellationToken)).Zone;

        foreach (var (workflow, spec, trigger, index) in timed)
        {
            var id = StateId(workflow.Id, index);
            if (!states.TryGetValue(id, out var state) || state.WorkflowVersion != workflow.CurrentVersion)
            {
                if (state is not null)
                {
                    db.Schedules.Remove(state);
                    await db.SaveChangesAsync(cancellationToken);
                }

                // Counting starts when the workflow (or this version) was saved or turned on.
                state = new WorkflowSchedule { Id = id, WorkflowVersion = workflow.CurrentVersion, CheckedUntil = workflow.UpdatedAt };
                db.Schedules.Add(state);
            }

            try
            {
                if (trigger.Type == WorkflowTriggers.Schedule)
                {
                    await ScheduleAsync(workflow, trigger, index, state, await ZoneAsync(trigger.TimeZone), now, cancellationToken);
                }
                else
                {
                    await DatesAsync(workflow, spec, trigger, index, state, await ZoneAsync(null), now, cancellationToken);
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) when (db.Entry(state).State == EntityState.Added)
            {
                // A concurrent scheduler initialized this trigger first. Its durable state and occurrence ids win.
                db.ChangeTracker.Clear();
                if (!await db.Schedules.AnyAsync(s => s.Id == id && s.WorkflowVersion == workflow.CurrentVersion, cancellationToken))
                {
                    throw;
                }

                return;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The state row of a workflow's timed trigger: the workflow's id for its first trigger (as before several triggers),
    /// else one derived from the trigger's position.
    /// </summary>
    internal static Guid StateId(Guid workflowId, int index) => index == 0 ? workflowId : TriggerSchedules.EventId(workflowId, "trigger", index);

    private async Task ScheduleAsync(
        WorkflowDefinition workflow, WorkflowTrigger trigger, int index, WorkflowSchedule state, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        if (TriggerSchedules.ParseCron(trigger.Cron) is not { } cron)
        {
            return;
        }

        state.NextAt ??= Next(cron, state.CheckedUntil ?? now, zone);
        if (state.NextAt is not { } due || due > now)
        {
            return;
        }

        var occurrence = due.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var eventId = index == 0 ? TriggerSchedules.EventId(workflow.Id, occurrence) : TriggerSchedules.EventId(workflow.Id, index, occurrence);
        if (!await db.Runs.AnyAsync(r => r.WorkflowId == workflow.Id && r.EventId == eventId, ct))
        {
            var data = new JsonObject { ["occurrence"] = due.ToString("O", CultureInfo.InvariantCulture) };
            await starter.StartAsync([new WorkflowStart(workflow, null, eventId, data.ToJsonString(), 0, null, Concurrency: trigger.Concurrency)], ct);
        }

        // Missed occurrences run once: the next one is after now.
        state.NextAt = Next(cron, now, zone);
        state.CheckedUntil = now;
    }

    private async Task DatesAsync(
        WorkflowDefinition workflow, WorkflowSpec spec, WorkflowTrigger trigger, int index, WorkflowSchedule state, TimeZoneInfo zone, DateTimeOffset now,
        CancellationToken ct)
    {
        var offset = TimeSpan.FromHours(trigger.OffsetHours ?? 0);
        var from = state.CheckedUntil ?? now;
        if (from >= now)
        {
            return;
        }

        var store = items.AsSystem();
        var list = (await store.GetListsAsync(workflow.WorkspaceId, null, ct)).FirstOrDefault(l => l.Name == trigger.List);
        var description = list is null ? null : await store.DescribeListAsync(workflow.WorkspaceId, list.Id, ct);
        var field = description?.ContentTypes.SelectMany(c => c.Fields).FirstOrDefault(f => f.Name == trigger.Field);
        if (list is null || field?.Type is not ("date" or "dateTime"))
        {
            // The list or field is gone or changed: nothing to start (the trigger is checked again when saved).
            state.CheckedUntil = now;
            return;
        }

        // An item is due when date + offset is in (from, now]; so its date is in (from - offset, now - offset].
        var (lower, upper) = field.Type == "date"
            ? (Literal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from - offset, zone).DateTime)),
               Literal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now - offset, zone).DateTime)))
            : (Literal(from - offset), Literal(now - offset));
        if (lower == upper)
        {
            // A date field within the same day: no date is in the range until the day changes.
            state.CheckedUntil = now;
            return;
        }

        var filter = $"fields/{field.Name} gt {lower} and fields/{field.Name} le {upper}" + (spec.Condition is { } condition ? $" and ({condition})" : string.Empty);
        string? cursor = null;
        do
        {
            var (page, error) = await store.QueryPageAsync(workflow.WorkspaceId, list.Id, new ListItemQuery(filter, $"fields/{field.Name}", PageSize, cursor), ct);
            if (page is null || error is not null)
            {
                // A condition that no longer works starts nothing (the workflow is checked again when saved).
                break;
            }

            var candidates = page.Items.Select(i => (Item: i, Value: i.Fields[field.Name]?.ToString() ?? string.Empty))
                .Select(c => (c.Item, c.Value, EventId: index == 0
                    ? TriggerSchedules.EventId(workflow.Id, c.Item.Id, c.Value)
                    : TriggerSchedules.EventId(workflow.Id, index, c.Item.Id, c.Value)))
                .ToList();
            var eventIds = candidates.Select(c => (Guid?)c.EventId).ToList();
            var started = (await db.Runs.Where(r => r.WorkflowId == workflow.Id && eventIds.Contains(r.EventId)).Select(r => r.EventId).ToListAsync(ct)).ToHashSet();
            await starter.StartAsync(
                [.. candidates.Where(c => !started.Contains(c.EventId)).Select(c => new WorkflowStart(
                    workflow, new WorkflowItem(workflow.WorkspaceId, list.Id, c.Item.Id), c.EventId,
                    new JsonObject { ["date"] = c.Value }.ToJsonString(), 0, null, Concurrency: trigger.Concurrency))],
                ct);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        state.CheckedUntil = now;
    }

    /// <summary>The next occurrence in UTC (Cronos returns the zone's offset; PostgreSQL stores UTC only).</summary>
    private static DateTimeOffset? Next(CronExpression cron, DateTimeOffset after, TimeZoneInfo zone) =>
        cron.GetNextOccurrence(after, zone)?.ToUniversalTime();

    private static string Literal(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Literal(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
