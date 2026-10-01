using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Calendar.Features;

public sealed record EventSeriesRequest(string? Rule, string? TimeZone);

public sealed record EventSeriesResponse(string Rule, string TimeZone, IReadOnlyList<DateTimeOffset> Cancelled, IReadOnlyList<OccurrenceOverrideResponse> Moved);

public sealed record OccurrenceOverrideResponse(DateTimeOffset OriginalStart, Guid ItemId);

/// <summary>Changes for one occurrence: <c>fields</c> are merged over the series' values (e.g. a new <c>start</c>).</summary>
public sealed record OccurrenceRequest(JsonObject? Fields);

public sealed record OccurrenceResponse(Guid ItemId, JsonObject Fields);

public sealed record CalendarResponse([property: JsonPropertyName("value")] IReadOnlyList<CalendarEntry> Value);

public sealed record FeedRequest(string? Name, Guid? WorkspaceId, Guid? ListId);

/// <summary>A calendar feed; <see cref="Url"/> (with the secret) is only returned when the feed is created.</summary>
public sealed record FeedResponse(
    Guid Id, string Name, Guid? WorkspaceId, Guid? ListId, DateTimeOffset CreatedAt, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url);

/// <summary>
/// Calendar endpoints: recurrence and exceptions (CAL-02), time ranges (CAL-03), iCalendar import, export and feeds
/// (CAL-04). Event items themselves are managed with the items API.
/// </summary>
internal static class CalendarEndpoints
{
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);
    private const int MaxImportBytes = 10 * 1024 * 1024;
    private static readonly string[] CopiedFields = ["title", "location", "description", "attendees", "allDay", "reminderMinutes"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var item = app.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/series").WithTags("Calendar");
        item.MapGet("", GetRecurrenceAsync).RequireScope(CalendarScopes.Read).WithName("GetEventRecurrence");
        item.MapPut("", SetRecurrenceAsync).RequireScope(CalendarScopes.Write).WithName("SetEventRecurrence")
            .WithDescription("Makes the event repeat (RFC 5545 RRULE) in an IANA time zone (default: the caller's preferred time zone).");
        item.MapDelete("", RemoveRecurrenceAsync).RequireScope(CalendarScopes.Write).WithName("RemoveEventRecurrence");
        item.MapPut("/occurrences/{occurrenceStart}", ChangeOccurrenceAsync).RequireScope(CalendarScopes.Write).WithName("ChangeOccurrence")
            .WithDescription("Moves or changes one occurrence (RECURRENCE-ID): it becomes its own event item with the series' values and the changes.");
        item.MapDelete("/occurrences/{occurrenceStart}", CancelOccurrenceAsync).RequireScope(CalendarScopes.Write).WithName("CancelOccurrence")
            .WithDescription("Cancels one occurrence of a series (EXDATE).");

        var list = app.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}").WithTags("Calendar");
        list.MapGet("/calendar", ListRangeAsync).RequireScope(CalendarScopes.Read).WithName("GetListCalendar");
        list.MapGet("/calendar.ics", ExportAsync).RequireScope(CalendarScopes.Read).WithName("ExportListCalendar").Produces(StatusCodes.Status200OK, contentType: ICalendarService.MediaType);
        list.MapPost("/calendar/import", ImportAsync).RequireScope(CalendarScopes.Write).WithName("ImportCalendar")
            .Accepts<string>(ICalendarService.MediaType)
            .WithDescription("Imports events from an iCalendar body (text/calendar, up to 10 MB) into an event list.");

        var me = app.MapGroup("/v1.0/me").WithTags("Calendar");
        me.MapGet("/calendar", MyRangeAsync).RequireScope(CalendarScopes.Read).WithName("GetMyCalendar")
            .WithDescription("Events and due tasks between start and end (ISO 8601, at most 366 days) across every calendar and task list the caller can read.");
        me.MapGet("/calendarFeeds", ListFeedsAsync).RequireScope(CalendarScopes.Read).WithName("ListCalendarFeeds");
        me.MapPost("/calendarFeeds", CreateFeedAsync).RequireScope(CalendarScopes.Write).WithName("CreateCalendarFeed")
            .WithDescription("A read-only subscription URL for calendar apps: one list, or all the caller's calendar and task lists. The URL contains a secret and is shown once.");
        me.MapDelete("/calendarFeeds/{id:guid}", DeleteFeedAsync).RequireScope(CalendarScopes.Write).WithName("DeleteCalendarFeed");

        app.MapGet("/v1.0/calendarFeeds/{token}", FeedAsync).AllowAnonymous().WithTags("Calendar").WithName("GetCalendarFeed")
            .Produces(StatusCodes.Status200OK, contentType: ICalendarService.MediaType)
            .WithDescription("The feed ({token}.ics, no sign-in: the token names the organization and the feed). Shows what the owner can read now.");
    }

    private static CalendarService Calendar(IListItemStore items, CalendarDbContext db, Caller caller) => new(items, db, caller.TenantId);

    // ---- Recurrence -----------------------------------------------------------

    private static Task<EventRecurrence?> FindRecurrenceAsync(CalendarDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        return db.Recurrences.FirstOrDefaultAsync(r => r.TenantId == tenant && r.ItemId == id, ct);
    }

    private static Task<OccurrenceChange?> FindExceptionAsync(CalendarDbContext database, Guid tenantId, Guid masterItemId, long startUnixMs, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var master = masterItemId;
        var start = startUnixMs;
        var ct = cancellationToken;
        return db.OccurrenceChanges.FirstOrDefaultAsync(e => e.TenantId == tenant && e.MasterItemId == master && e.OriginalStartUnixMs == start, ct);
    }

    private static async Task<Results<Ok<EventSeriesResponse>, ProblemHttpResult>> GetRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, CalendarDbContext db, CancellationToken ct)
    {
        var recurrence = await CalendarAccess.EventAsync(items, workspaceId, listId, itemId, ct) is null ? null : await FindRecurrenceAsync(db, caller.TenantId, itemId, ct);
        return recurrence is null ? ApiErrors.NotFound("The event does not repeat.") : TypedResults.Ok(await ResponseAsync(db, recurrence, ct));
    }

    private static async Task<Results<Ok<EventSeriesResponse>, ValidationProblem, ProblemHttpResult>> SetRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, EventSeriesRequest request, Caller caller, IListItemStore items, CalendarDbContext database,
        IUserPreferences preferences, CancellationToken cancellationToken)
    {
        var rule = request.Rule?.Trim() ?? string.Empty;
        rule = rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? rule[6..] : rule;
        var zone = !string.IsNullOrWhiteSpace(request.TimeZone) ? request.TimeZone.Trim() : (await preferences.GetAsync(caller.TenantId, caller.UserId, cancellationToken)).TimeZone;
        var errors = new Dictionary<string, string[]>();
        if (rule.Length is 0 or > 500 || !CalendarService.IsValidRule(rule))
        {
            errors["rule"] = ["A valid RRULE is expected, e.g. FREQ=WEEKLY;BYDAY=MO."];
        }

        if (zone.Length > 64 || !CalendarService.IsKnownTimeZone(zone))
        {
            errors["timeZone"] = ["An IANA time zone is expected, e.g. Europe/Berlin."];
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(errors);
        }

        var db = database;
        var tenant = caller.TenantId;
        var id = itemId;
        var ct = cancellationToken;
        var item = await CalendarAccess.EventAsync(items, workspaceId, listId, itemId, ct);
        if (item is null || await db.OccurrenceChanges.AnyAsync(e => e.TenantId == tenant && e.OverrideItemId == id, ct))
        {
            return ApiErrors.NotFound();
        }

        if (item.Access < WorkspaceAccessLevel.Contribute)
        {
            return CalendarAccess.Forbidden();
        }

        var recurrence = await FindRecurrenceAsync(db, tenant, id, ct);
        if (recurrence is null)
        {
            recurrence = new EventRecurrence { Id = Ids.New(), TenantId = tenant, ItemId = id, WorkspaceId = workspaceId, ListId = listId };
            db.Recurrences.Add(recurrence);
        }

        recurrence.Rule = rule;
        recurrence.TimeZone = zone;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ResponseAsync(db, recurrence, ct));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, CalendarDbContext database, CancellationToken cancellationToken)
    {
        var item = await CalendarAccess.EventAsync(items, workspaceId, listId, itemId, cancellationToken);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (item.Access < WorkspaceAccessLevel.Contribute)
        {
            return CalendarAccess.Forbidden();
        }

        var db = database;
        var tenant = caller.TenantId;
        var id = itemId;
        var ct = cancellationToken;
        db.Recurrences.RemoveRange(await db.Recurrences.Where(r => r.TenantId == tenant && r.ItemId == id).ToListAsync(ct));
        db.OccurrenceChanges.RemoveRange(await db.OccurrenceChanges.Where(e => e.TenantId == tenant && e.MasterItemId == id && e.OverrideItemId == null).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> CancelOccurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, string occurrenceStart, Caller caller, IListItemStore items, CalendarDbContext db, CancellationToken ct)
    {
        if (!EventTimes.TryParse(occurrenceStart, out var start))
        {
            return ApiErrors.Validation("occurrenceStart", "An ISO 8601 date and time is expected.");
        }

        var (series, problem) = await CalendarAccess.OccurrenceAsync(items, db, caller.TenantId, workspaceId, listId, itemId, start, ct);
        if (problem is not null)
        {
            return problem;
        }

        var startMs = start.ToUnixTimeMilliseconds();
        var exception = await FindExceptionAsync(db, caller.TenantId, itemId, startMs, ct);
        if (exception is null)
        {
            db.OccurrenceChanges.Add(new OccurrenceChange { Id = Ids.New(), TenantId = caller.TenantId, ListId = listId, MasterItemId = series!.Id, OriginalStartUnixMs = startMs });
        }
        else
        {
            exception.OverrideItemId = null; // A moved occurrence is cancelled too; its item stays as a single event.
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<OccurrenceResponse>, ValidationProblem, ProblemHttpResult>> ChangeOccurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, string occurrenceStart, OccurrenceRequest request, Caller caller,
        IListItemStore items, CalendarDbContext db, CancellationToken ct)
    {
        if (!EventTimes.TryParse(occurrenceStart, out var start))
        {
            return ApiErrors.Validation("occurrenceStart", "An ISO 8601 date and time is expected.");
        }

        var (series, problem) = await CalendarAccess.OccurrenceAsync(items, db, caller.TenantId, workspaceId, listId, itemId, start, ct);
        if (problem is not null)
        {
            return problem;
        }

        var startMs = start.ToUnixTimeMilliseconds();
        var exception = await FindExceptionAsync(db, caller.TenantId, itemId, startMs, ct);
        var changes = request.Fields ?? [];
        ListItemResult result;
        if (exception?.OverrideItemId is { } overrideId && await items.GetAsync(workspaceId, listId, overrideId, ct) is not null)
        {
            result = await items.UpdateAsync(workspaceId, listId, overrideId, changes, null, ct);
        }
        else
        {
            var times = EventTimes.From(series!.Fields)!;
            var fields = new JsonObject();
            foreach (var name in CopiedFields.Where(n => series.Fields[n] is not null))
            {
                fields[name] = series.Fields[name]!.DeepClone();
            }

            fields["start"] = FieldFormats.DateTime(start);
            fields["end"] = FieldFormats.DateTime(start + times.Duration);
            foreach (var (name, value) in changes)
            {
                fields[name] = value?.DeepClone();
            }

            // A new start without an end keeps the series' duration.
            if (changes.ContainsKey("start") && !changes.ContainsKey("end") && EventTimes.TryParse(fields["start"], out var movedStart))
            {
                fields["end"] = FieldFormats.DateTime(movedStart + times.Duration);
            }

            result = await items.CreateAsync(workspaceId, listId, fields, series.ContentTypeId, ct);
            if (result.Succeeded)
            {
                if (exception is null)
                {
                    db.OccurrenceChanges.Add(new OccurrenceChange
                    {
                        Id = Ids.New(),
                        TenantId = caller.TenantId,
                        ListId = listId,
                        MasterItemId = itemId,
                        OriginalStartUnixMs = startMs,
                        OverrideItemId = result.Item!.Id,
                    });
                }
                else
                {
                    exception.OverrideItemId = result.Item!.Id;
                }

                await db.SaveChangesAsync(ct);
            }
        }

        return result.Status switch
        {
            ListItemStatus.Ok => TypedResults.Ok(new OccurrenceResponse(result.Item!.Id, (JsonObject)result.Item.Fields.DeepClone())),
            ListItemStatus.Invalid => ApiErrors.Validation(result.Errors!.ToDictionary()),
            ListItemStatus.Forbidden => CalendarAccess.Forbidden(),
            ListItemStatus.NotFound => ApiErrors.NotFound(),
            _ => ApiErrors.Conflict("rejected", result.Message ?? "The change was rejected."),
        };
    }

    private static async Task<EventSeriesResponse> ResponseAsync(CalendarDbContext database, EventRecurrence recurrence, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = recurrence.TenantId;
        var master = recurrence.ItemId;
        var ct = cancellationToken;
        var exceptions = await db.OccurrenceChanges.AsNoTracking().Where(e => e.TenantId == tenant && e.MasterItemId == master).OrderBy(e => e.OriginalStartUnixMs).ToListAsync(ct);
        return new EventSeriesResponse(
            recurrence.Rule,
            recurrence.TimeZone,
            [.. exceptions.Where(e => e.OverrideItemId is null).Select(e => e.OriginalStart)],
            [.. exceptions.Where(e => e.OverrideItemId is not null).Select(e => new OccurrenceOverrideResponse(e.OriginalStart, e.OverrideItemId!.Value))]);
    }

    // ---- Time ranges ------------------------------------------------------------

    private static async Task<Results<Ok<CalendarResponse>, ValidationProblem>> MyRangeAsync(
        DateTimeOffset? start, DateTimeOffset? end, bool? includeTasks, Caller caller, IListItemStore items, CalendarDbContext db, CancellationToken ct)
    {
        if (RangeProblem(start, end) is { } problem)
        {
            return problem;
        }

        var calendar = Calendar(items, db, caller);
        var lists = await calendar.ListsAsync(null, null, ct);
        var (entries, error) = await calendar.RangeAsync(lists, start!.Value, end!.Value, includeTasks ?? true, ct);
        return error is null ? TypedResults.Ok(new CalendarResponse(entries)) : ApiErrors.Validation("filter", error);
    }

    private static async Task<Results<Ok<CalendarResponse>, ValidationProblem, ProblemHttpResult>> ListRangeAsync(
        Guid workspaceId, Guid listId, DateTimeOffset? start, DateTimeOffset? end, bool? includeTasks, Caller caller, IListItemStore items, CalendarDbContext db, CancellationToken ct)
    {
        if (RangeProblem(start, end) is { } problem)
        {
            return problem;
        }

        var calendar = Calendar(items, db, caller);
        var lists = await calendar.ListsAsync(workspaceId, listId, ct);
        if (lists.Count == 0)
        {
            return ApiErrors.NotFound("The calendar or task list was not found.");
        }

        var (entries, error) = await calendar.RangeAsync(lists, start!.Value, end!.Value, includeTasks ?? true, ct);
        return error is null ? TypedResults.Ok(new CalendarResponse(entries)) : ApiErrors.Validation("filter", error);
    }

    private static ValidationProblem? RangeProblem(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null || end <= start || end - start > MaxRange
            ? ApiErrors.Validation("range", "start and end (ISO 8601) are required; end after start, at most 366 days.")
            : null;

    // ---- iCalendar ------------------------------------------------------------------

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ExportAsync(
        Guid workspaceId, Guid listId, Caller caller, IListItemStore items, CalendarDbContext db, CancellationToken ct)
    {
        var lists = await Calendar(items, db, caller).ListsAsync(workspaceId, listId, ct);
        if (lists.Count == 0)
        {
            return ApiErrors.NotFound("The calendar or task list was not found.");
        }

        var (text, error) = await new ICalendarService(items, db, caller.TenantId).ExportAsync(lists, ct);
        return error is null
            ? TypedResults.Text(text, ICalendarService.MediaType, Encoding.UTF8)
            : ApiErrors.Problem(StatusCodes.Status400BadRequest, "invalidFilter", error);
    }

    private static async Task<Results<Ok<ImportResult>, ValidationProblem, ProblemHttpResult>> ImportAsync(
        Guid workspaceId, Guid listId, HttpRequest http, Caller caller, IListItemStore items, CalendarDbContext db, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        if (list is null || !list.ContentTypeKeys.Contains(CalendarService.EventKey))
        {
            return ApiErrors.NotFound("The calendar was not found.");
        }

        if (list.Access < WorkspaceAccessLevel.Contribute)
        {
            return CalendarAccess.Forbidden();
        }

        using var reader = new StreamReader(http.Body, Encoding.UTF8);
        var buffer = new char[MaxImportBytes + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (length > MaxImportBytes)
        {
            return ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "fileTooLarge", "Calendars may have at most 10 MB.");
        }

        var result = await new ICalendarService(items, db, caller.TenantId).ImportAsync(list, new string(buffer, 0, length), ct);
        return result.Created + result.Updated == 0 && result.Errors.Count > 0 && result.Skipped == 0
            ? ApiErrors.Validation(new Dictionary<string, string[]> { ["calendar"] = [.. result.Errors] })
            : TypedResults.Ok(result);
    }

    // ---- Feeds --------------------------------------------------------------------

    private static async Task<Ok<List<FeedResponse>>> ListFeedsAsync(Caller caller, CalendarDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var ct = cancellationToken;
        var feeds = await db.Feeds.AsNoTracking().Where(f => f.TenantId == tenant && f.UserId == user).OrderBy(f => f.Id).ToListAsync(ct);
        return TypedResults.Ok(feeds.Select(f => new FeedResponse(f.Id, f.Name, f.WorkspaceId, f.ListId, f.CreatedAt, null)).ToList());
    }

    private static async Task<Results<Created<FeedResponse>, ValidationProblem, ProblemHttpResult>> CreateFeedAsync(
        FeedRequest request, Caller caller, IListItemStore items, CalendarDbContext db, HttpRequest http, CancellationToken ct)
    {
        if ((request.ListId is null) != (request.WorkspaceId is null) || (request.Name?.Length ?? 0) > 200)
        {
            return ApiErrors.Validation("listId", "Give both workspaceId and listId, or neither; names have at most 200 characters.");
        }

        if (request.ListId is not null && (await Calendar(items, db, caller).ListsAsync(request.WorkspaceId, request.ListId, ct)).Count == 0)
        {
            return ApiErrors.NotFound("The calendar or task list was not found.");
        }

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var feed = new CalendarFeed
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            UserId = caller.UserId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Calendar" : request.Name.Trim(),
            WorkspaceId = request.WorkspaceId,
            ListId = request.ListId,
            SecretHash = Hash(secret),
        };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(ct);
        var url = $"{http.Scheme}://{http.Host}{http.PathBase}/v1.0/calendarFeeds/{caller.TenantId:N}{secret}.ics";
        return TypedResults.Created($"/v1.0/me/calendarFeeds/{feed.Id}", new FeedResponse(feed.Id, feed.Name, feed.WorkspaceId, feed.ListId, feed.CreatedAt, url));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteFeedAsync(Guid id, Caller caller, CalendarDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var user = caller.UserId;
        var feedId = id;
        var ct = cancellationToken;
        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.TenantId == tenant && f.Id == feedId && f.UserId == user, ct);
        if (feed is null)
        {
            return ApiErrors.NotFound();
        }

        db.Feeds.Remove(feed);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The feed (no sign-in: the token names the tenant and the feed). Runs as the feed's owner, so it shows what they can
    /// read now; revoked feeds and deactivated owners answer 404.
    /// </summary>
    private static async Task<Results<ContentHttpResult, NotFound>> FeedAsync(
        string token, IListItemStore items, CalendarDbContext database, IUserDirectory users, CancellationToken cancellationToken)
    {
        var value = token.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ? token[..^4] : token;
        if (value.Length < 40 || !Guid.TryParseExact(value[..32], "N", out var tenantId))
        {
            return TypedResults.NotFound();
        }

        var db = database;
        var tenant = tenantId;
        var hash = Hash(value[32..]);
        var ct = cancellationToken;
        var feed = await db.Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.TenantId == tenant && f.SecretHash == hash, ct);
        if (feed is null || !await users.IsActiveAsync(tenant, feed.UserId, ct))
        {
            return TypedResults.NotFound();
        }

        var owner = items.ActingAs(new ChangeActor(tenant, feed.UserId));
        var lists = await new CalendarService(owner, db, tenant).ListsAsync(feed.WorkspaceId, feed.ListId, ct);
        var (text, error) = await new ICalendarService(owner, db, tenant).ExportAsync(lists, ct);
        return error is null ? TypedResults.Text(text, ICalendarService.MediaType, Encoding.UTF8) : TypedResults.NotFound();
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

/// <summary>Resolves events with the caller's access, via the lists engine.</summary>
internal static class CalendarAccess
{
    public static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    /// <summary>The item when it is readable and its list has the event content type.</summary>
    public static async Task<ListItemData?> EventAsync(IListItemStore items, Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        return list is null || !list.ContentTypeKeys.Contains(CalendarService.EventKey) ? null : await items.GetAsync(workspaceId, listId, itemId, ct);
    }

    /// <summary>The series when <paramref name="occurrenceStart"/> is one of its occurrences and the caller may change it.</summary>
    public static async Task<(ListItemData? Series, ProblemHttpResult? Problem)> OccurrenceAsync(
        IListItemStore items, CalendarDbContext database, Guid tenantId, Guid workspaceId, Guid listId, Guid itemId, DateTimeOffset occurrenceStart, CancellationToken cancellationToken)
    {
        var series = await EventAsync(items, workspaceId, listId, itemId, cancellationToken);
        var db = database;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        var recurrence = series is null ? null : await db.Recurrences.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenant && r.ItemId == id, ct);
        if (recurrence is null || EventTimes.From(series!.Fields) is not { } times)
        {
            return (null, ApiErrors.NotFound("The event does not repeat."));
        }

        if (series.Access < WorkspaceAccessLevel.Contribute)
        {
            return (null, Forbidden());
        }

        var start = occurrenceStart.ToUniversalTime();
        return CalendarService.Occurrences(recurrence, times, start.AddTicks(1)).Contains(start)
            ? (series, null)
            : (null, ApiErrors.NotFound("The series has no occurrence at that time."));
    }
}
