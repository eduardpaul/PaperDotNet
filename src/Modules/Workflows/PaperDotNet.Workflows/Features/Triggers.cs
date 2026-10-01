using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cronos;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

/// <summary>Trigger types workflows can use: the built-in ones and those modules and extensions register.</summary>
public sealed class TriggerCatalog(IEnumerable<WorkflowTriggerDefinition> registered)
{
    public static readonly WorkflowTriggerDefinition[] BuiltIn =
    [
        new(WorkflowTriggers.Manual, "Started by a person with Contribute access (POST …/workflows/{id}/runs), on items of list or without an item."),
        new(WorkflowTriggers.ItemAdded, "An item or document was added to a list."),
        new(WorkflowTriggers.ItemUpdated, "An item was changed (optionally only when one of changedFields changed)."),
        new(WorkflowTriggers.ItemDeleted, "An item was moved to the recycle bin."),
        new(WorkflowTriggers.ItemRestored, "An item was restored from the recycle bin."),
        new(WorkflowTriggers.Schedule, "On a schedule: cron (5 fields, e.g. 0 8 * * 1-5) in timeZone (default: the organization's); no item."),
        new(WorkflowTriggers.Date, "For each item of list when its date field plus offsetHours (negative: before) is reached; once per item and date."),
        new(WorkflowTriggerKeys.ApprovalDecided, "An approval of a workflow run was decided (data: workflow, step, outcome, comment, decidedBy)."),
    ];

    public IReadOnlyList<WorkflowTriggerDefinition> All { get; } =
        [.. BuiltIn, .. registered.DistinctBy(t => t.Key).OrderBy(t => t.Key, StringComparer.Ordinal)];

    /// <summary>Whether a trigger type is known: listed, or a workflow event (<c>wf.{key}.{event}</c>).</summary>
    public bool IsKnown(string type) => All.Any(t => t.Key == type) || WorkflowEvents.IsEventTrigger(type);
}

/// <summary>
/// Raises module and extension triggers (<see cref="IWorkflowTriggers"/>): a <see cref="WorkflowTriggerRaised"/> event
/// saved through the outbox in its own scope, so raising does not save (or clear) the caller's changes.
/// </summary>
internal sealed class WorkflowTriggerPublisher(IServiceScopeFactory scopes, TimeProvider time) : IWorkflowTriggers
{
    public Task RaiseAsync(ChangeActor actor, string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, Guid eventId, CancellationToken cancellationToken) =>
        PublishAsync(new WorkflowTriggerRaised
        {
            EventId = eventId,
            TenantId = actor.TenantId,
            UserId = actor.UserId,
            Depth = actor.Depth,
            OccurredAt = time.GetUtcNow(),
            Trigger = triggerKey,
            WorkspaceId = workspaceId,
            ListId = item?.ListId,
            ItemId = item?.ItemId,
            Data = data?.ToJsonString(),
        }, cancellationToken);

    public Task RaiseAsync(string triggerKey, Guid workspaceId, WorkflowItem? item, JsonObject? data, IntegrationEvent cause, CancellationToken cancellationToken) =>
        RaiseAsync(new ChangeActor(cause.TenantId, cause.UserId, cause.Depth), triggerKey, workspaceId, item, data,
            WorkflowStarter.StableId($"trigger:{cause.EventId:N}:{triggerKey}"), cancellationToken);

    private async Task PublishAsync(WorkflowTriggerRaised raised, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        await scope.ServiceProvider.GetRequiredService<IOutbox>().SaveChangesAsync(db, [raised], [], cancellationToken);
    }
}

/// <summary>The inputs a person gives when starting a manual workflow: described by a small JSON Schema object.</summary>
internal static class WorkflowInputs
{
    private static readonly string[] Types = ["string", "number", "integer", "boolean", "array", "object"];

    /// <summary>Checks the schema itself when a workflow is saved.</summary>
    public static IEnumerable<string> ValidateSchema(JsonObject? schema)
    {
        if (schema is null)
        {
            yield break;
        }

        if (schema["properties"] is not JsonObject properties)
        {
            yield return "inputs needs properties (a JSON Schema object).";
            yield break;
        }

        foreach (var (name, property) in properties)
        {
            if ((property as JsonObject)?["type"] is not JsonValue type || !Types.Contains(type.ToString()))
            {
                yield return $"inputs.{name} needs a type ({string.Join(", ", Types)}).";
            }
        }

        foreach (var required in schema["required"] as JsonArray ?? [])
        {
            if (required?.ToString() is not { } name || !properties.ContainsKey(name))
            {
                yield return $"inputs.required names '{required}', which is not a property.";
            }
        }
    }

    /// <summary>Why the values do not fit the schema, or null (no schema: any values).</summary>
    public static string? Check(JsonObject? schema, JsonObject? values)
    {
        if (schema?["properties"] is not JsonObject properties)
        {
            return null;
        }

        foreach (var required in schema["required"] as JsonArray ?? [])
        {
            if (required?.ToString() is { } name && values?[name] is null)
            {
                return $"The input '{name}' is required.";
            }
        }

        foreach (var (name, value) in values ?? [])
        {
            if ((properties[name] as JsonObject)?["type"] is not JsonValue declared || declared.ToString() is not { } type)
            {
                return $"Unknown input '{name}'.";
            }

            var kind = value?.GetValueKind();
            var fits = type switch
            {
                "string" => kind == JsonValueKind.String,
                "number" => kind == JsonValueKind.Number,
                "integer" => kind == JsonValueKind.Number && value!.ToJsonString().All(c => char.IsAsciiDigit(c) || c == '-'),
                "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
                "array" => kind == JsonValueKind.Array,
                _ => kind == JsonValueKind.Object,
            };
            if (value is not null && !fits)
            {
                return $"The input '{name}' must be of type {type}.";
            }
        }

        return null;
    }
}

/// <summary>Terms of triggers (<c>terms</c>: paths <c>Group/Set/Term</c>): checked when saved, matched against item values.</summary>
public sealed class TriggerTerms(ITermStore terms)
{
    /// <summary>Errors for term paths that do not exist in the tenant.</summary>
    public async Task<List<string>> CheckAsync(Guid tenantId, WorkflowSpec spec, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var path in spec.AllTriggers.SelectMany(t => t.Terms ?? []).Distinct(StringComparer.Ordinal))
        {
            if (await terms.FindTermByPathAsync(tenantId, path, cancellationToken) is null)
            {
                errors.Add($"The term '{path}' does not exist (use Group/Set/Term).");
            }
        }

        return errors;
    }

    /// <summary>
    /// Whether a value of the item is one of the terms or a term below one of them. Terms are looked for in every field, so
    /// the trigger does not depend on field names.
    /// </summary>
    public async Task<bool> HasAnyAsync(Guid tenantId, ListItemData? item, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return false;
        }

        var wanted = new HashSet<Guid>();
        foreach (var path in paths)
        {
            if (await terms.FindTermByPathAsync(tenantId, path, cancellationToken) is { } id)
            {
                wanted.Add(id);
            }
        }

        if (wanted.Count == 0)
        {
            return false;
        }

        foreach (var descendants in (await terms.GetDescendantsAsync(tenantId, wanted, cancellationToken)).Values)
        {
            wanted.UnionWith(descendants);
        }

        return item.Fields.Select(f => f.Value)
            .SelectMany(value => value is JsonArray array ? array.AsEnumerable() : [value])
            .Any(value => value is JsonValue text && text.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) && wanted.Contains(id));
    }
}

/// <summary>Cron expressions of schedule triggers.</summary>
internal static class TriggerSchedules
{
    /// <summary>A 5-field cron expression, or null when it is not one.</summary>
    public static CronExpression? ParseCron(string? cron) =>
        cron is { Length: > 0 } && cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 5
            && CronExpression.TryParse(cron, CronFormat.Standard, out var expression)
            ? expression
            : null;

    /// <summary>The next occurrence in UTC.</summary>
    public static DateTimeOffset? Next(CronExpression cron, DateTimeOffset after, TimeZoneInfo zone) =>
        cron.GetNextOccurrence(after, zone)?.ToUniversalTime();
}

/// <summary>
/// Every minute, starts the workflows of timed triggers (EVT-10, EVT-11, ADR-0036):
/// <list type="bullet">
/// <item><c>schedule</c>: one run per occurrence of the cron expression, in the trigger's time zone or the
/// organization's. Occurrences missed while the server was down run once.</item>
/// <item><c>date</c>: one run per item and date value when the item's date field plus <c>offsetHours</c> is reached
/// (dates without a time count from midnight in the organization's time zone). Only moments after the workflow was
/// saved count, so a new workflow does not run for dates in the past.</item>
/// </list>
/// The state per timed trigger is a <see cref="WorkflowSchedule"/> row; a new version starts over.
/// </summary>
internal sealed class WorkflowScheduleJob(
    WorkflowsDbContext db, WorkflowStarter starter, WorkflowItems items, IUserPreferences preferences, TimeProvider time) : ITenantRecurringJob
{
    public const string Name = "workflows.schedules";
    public const string Schedule = "* * * * *";

    /// <summary>Items read per page of a date trigger's query.</summary>
    private const int PageSize = 500;

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var workflows = (await TimedAsync(db, tenantId, $",{WorkflowTriggers.Schedule},", cancellationToken))
            .Concat(await TimedAsync(db, tenantId, $",{WorkflowTriggers.Date},", cancellationToken))
            .DistinctBy(w => w.Id)
            .ToList();
        var states = (await StatesAsync(db, tenantId, cancellationToken)).ToDictionary(s => s.Id);

        var timed = new List<(WorkflowDefinition Workflow, WorkflowSpec Spec, WorkflowTrigger Trigger, int Index)>();
        foreach (var workflow in workflows)
        {
            if (await WorkflowVersions.FindAsync(db, tenantId, workflow.Id, workflow.CurrentVersion, cancellationToken) is { } version)
            {
                var spec = WorkflowJson.Deserialize(version.Definition);
                timed.AddRange(spec.AllTriggers.Select((t, i) => (workflow, spec, t, i)).Where(t => t.t.Type is WorkflowTriggers.Schedule or WorkflowTriggers.Date));
            }
        }

        // Triggers of workflows that were turned off, deleted or changed start over when they come back.
        var active = timed.Select(t => StateId(t.Workflow.Id, t.Index)).ToHashSet();
        db.WorkflowSchedules.RemoveRange(states.Values.Where(s => !active.Contains(s.Id)));

        TimeZoneInfo? organizationZone = null;
        async Task<TimeZoneInfo> ZoneAsync(string? zone) =>
            zone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var found)
                ? found
                : organizationZone ??= (await preferences.GetDefaultsAsync(tenantId, cancellationToken)).Zone;

        foreach (var (workflow, spec, trigger, index) in timed)
        {
            var id = StateId(workflow.Id, index);
            if (!states.TryGetValue(id, out var state) || state.WorkflowVersion != workflow.CurrentVersion)
            {
                if (state is not null)
                {
                    db.WorkflowSchedules.Remove(state);
                    await db.SaveChangesAsync(cancellationToken);
                }

                // Counting starts when the workflow (or this version) was saved.
                state = new WorkflowSchedule { Id = id, TenantId = tenantId, WorkflowVersion = workflow.CurrentVersion, CheckedUntilUnixMs = workflow.UpdatedAt.ToUnixTimeMilliseconds() };
                db.WorkflowSchedules.Add(state);
            }

            if (trigger.Type == WorkflowTriggers.Schedule)
            {
                await ScheduleAsync(workflow, spec, trigger, index, state, await ZoneAsync(trigger.TimeZone), now, cancellationToken);
            }
            else
            {
                await DatesAsync(workflow, spec, trigger, index, state, await ZoneAsync(null), now, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The state row of a workflow's timed trigger: made from the workflow and the trigger's position.</summary>
    internal static Guid StateId(Guid workflowId, int index) => WorkflowStarter.StableId($"schedule:{workflowId:N}:{index}");

    private async Task ScheduleAsync(
        WorkflowDefinition workflow, WorkflowSpec spec, WorkflowTrigger trigger, int index, WorkflowSchedule state, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        if (TriggerSchedules.ParseCron(trigger.Cron) is not { } cron)
        {
            return;
        }

        var checkedUntil = state.CheckedUntilUnixMs is { } checkedMs ? DateTimeOffset.FromUnixTimeMilliseconds(checkedMs) : now;
        state.NextAtUnixMs ??= TriggerSchedules.Next(cron, checkedUntil, zone)?.ToUnixTimeMilliseconds();
        if (state.NextAtUnixMs is not { } dueMs || dueMs > now.ToUnixTimeMilliseconds())
        {
            return;
        }

        var due = DateTimeOffset.FromUnixTimeMilliseconds(dueMs);
        var occurrence = due.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        await starter.StartTimedAsync(workflow, spec, WorkflowTriggers.Schedule, WorkflowStarter.StableId($"schedule:{workflow.Id:N}:{index}:{occurrence}"),
            null, new JsonObject { ["occurrence"] = due.ToString("O", CultureInfo.InvariantCulture) }, ct);

        // Missed occurrences run once: the next one is after now.
        state.NextAtUnixMs = TriggerSchedules.Next(cron, now, zone)?.ToUnixTimeMilliseconds();
        state.CheckedUntilUnixMs = now.ToUnixTimeMilliseconds();
    }

    private async Task DatesAsync(
        WorkflowDefinition workflow, WorkflowSpec spec, WorkflowTrigger trigger, int index, WorkflowSchedule state, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        var offset = TimeSpan.FromHours(trigger.OffsetHours ?? 0);
        var from = state.CheckedUntilUnixMs is { } checkedMs ? DateTimeOffset.FromUnixTimeMilliseconds(checkedMs) : now;
        if (from >= now)
        {
            return;
        }

        var reader = new ChangeActor(workflow.TenantId, null);
        var list = await items.FindListByNameAsync(reader, workflow.WorkspaceId, trigger.List ?? "", ct);
        var description = list is null ? null : await items.DescribeListAsync(reader, workflow.WorkspaceId, list.Id, ct);
        var field = description?.ContentTypes.SelectMany(c => c.Fields).FirstOrDefault(f => f.Name == trigger.Field);
        if (list is null || field?.Type is not ("date" or "dateTime"))
        {
            // The list or field is gone or changed: nothing to start.
            state.CheckedUntilUnixMs = now.ToUnixTimeMilliseconds();
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
            state.CheckedUntilUnixMs = now.ToUnixTimeMilliseconds();
            return;
        }

        var filter = $"fields/{field.Name} gt {lower} and fields/{field.Name} le {upper}" + (spec.Condition is { } condition ? $" and ({condition})" : "");
        string? cursor = null;
        do
        {
            var (page, error) = await items.QueryPageAsync(reader, workflow.WorkspaceId, list.Id, new ListItemQuery(filter, $"fields/{field.Name}", PageSize, cursor), ct);
            if (page is null || error is not null)
            {
                break;
            }

            foreach (var item in page.Items)
            {
                var value = item.Fields[field.Name]?.ToString() ?? "";
                await starter.StartTimedAsync(workflow, spec, WorkflowTriggers.Date, WorkflowStarter.StableId($"date:{workflow.Id:N}:{index}:{item.Id:N}:{value}"),
                    item.Id, new JsonObject { ["date"] = value }, ct, list.Id);
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        state.CheckedUntilUnixMs = now.ToUnixTimeMilliseconds();
    }

    private static Task<List<WorkflowDefinition>> TimedAsync(WorkflowsDbContext database, Guid tenantId, string pattern, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var type = pattern;
        var ct = cancellationToken;
        return context.Workflows.AsNoTracking().Where(w => w.TenantId == tenant && w.Enabled && w.TriggerTypes.Contains(type)).ToListAsync(ct);
    }

    private static Task<List<WorkflowSchedule>> StatesAsync(WorkflowsDbContext database, Guid tenantId, CancellationToken cancellationToken)
    {
        var context = database;
        var tenant = tenantId;
        var ct = cancellationToken;
        return context.WorkflowSchedules.Where(s => s.TenantId == tenant).ToListAsync(ct);
    }

    private static string Literal(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Literal(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
