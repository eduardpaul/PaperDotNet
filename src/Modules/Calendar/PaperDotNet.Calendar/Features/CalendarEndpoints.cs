using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Calendar.Features;

public sealed record RecurrenceRequest(string Rule, string? TimeZone);

public sealed record RecurrenceResponse(string Rule, string TimeZone, IReadOnlyList<DateTimeOffset> Cancelled, IReadOnlyList<OverrideResponse> Moved);

public sealed record OverrideResponse(DateTimeOffset OriginalStart, Guid ItemId);

/// <summary>Changes for one occurrence: <c>fields</c> are merged over the series' values (e.g. a new <c>start</c>).</summary>
public sealed record OccurrenceRequest(JsonObject? Fields);

public sealed record CalendarResponse([property: JsonPropertyName("value")] IReadOnlyList<CalendarEntry> Value);

public sealed record FeedRequest(string? Name, Guid? WorkspaceId, Guid? ListId);

/// <summary>A calendar feed; <see cref="Url"/> (with the secret) is only returned when the feed is created.</summary>
public sealed record FeedResponse(Guid Id, string Name, Guid? WorkspaceId, Guid? ListId, DateTimeOffset CreatedAt, string? Url);

/// <summary>
/// Calendar endpoints: recurrence and exceptions (CAL-02), time ranges (CAL-03), iCalendar import,
/// export and feeds (CAL-04). Event items themselves are managed with the items API.
/// </summary>
internal static class CalendarEndpoints
{
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);
    private const int MaxImportBytes = 10 * 1024 * 1024;
    private static readonly string[] CopiedFields = ["title", "location", "description", "attendees", "allDay", "reminderMinutes"];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var item = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}", "Calendar");
        item.MapGet("/series", GetRecurrenceAsync).RequireScope(CalendarScopes.Read).WithName("GetEventRecurrence");
        item.MapPut("/series", SetRecurrenceAsync).RequireScope(CalendarScopes.Write).WithName("SetEventRecurrence");
        item.MapDelete("/series", RemoveRecurrenceAsync).RequireScope(CalendarScopes.Write).WithName("RemoveEventRecurrence");
        item.MapPut("/series/occurrences/{occurrenceStart:datetime}", ChangeOccurrenceAsync).RequireScope(CalendarScopes.Write).WithName("ChangeOccurrence");
        item.MapDelete("/series/occurrences/{occurrenceStart:datetime}", CancelOccurrenceAsync).RequireScope(CalendarScopes.Write).WithName("CancelOccurrence");

        var list = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}", "Calendar");
        list.MapGet("/calendar", ListRangeAsync).RequireScope(CalendarScopes.Read).WithName("GetListCalendar");
        list.MapGet("/calendar.ics", ExportAsync).RequireScope(CalendarScopes.Read).WithName("ExportListCalendar").ProducesBinary("text/calendar");
        list.MapPost("/calendar/import", ImportAsync).RequireScope(CalendarScopes.Write).WithName("ImportCalendar");

        var me = endpoints.MapV1Group("me", "Calendar");
        me.MapGet("/calendar", MyRangeAsync).RequireScope(CalendarScopes.Read).WithName("GetMyCalendar");
        me.MapGet("/calendarFeeds", ListFeedsAsync).RequireScope(CalendarScopes.Read).WithName("ListCalendarFeeds");
        me.MapPost("/calendarFeeds", CreateFeedAsync).RequireScope(CalendarScopes.Write).WithName("CreateCalendarFeed");
        me.MapDelete("/calendarFeeds/{id:guid}", DeleteFeedAsync).RequireScope(CalendarScopes.Write).WithName("DeleteCalendarFeed");

        endpoints.MapV1Group("calendarFeeds", "Calendar")
            .MapGet("/{token}.ics", FeedAsync)
            .AllowAnonymous()
            .WithName("GetCalendarFeed")
            .ProducesBinary("text/calendar");
    }

    // ---- Recurrence -----------------------------------------------------------

    private static async Task<Results<Ok<RecurrenceResponse>, ProblemHttpResult>> GetRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, CalendarAccess access, CalendarDbContext db, CancellationToken ct)
    {
        var recurrence = await access.EventAsync(workspaceId, listId, itemId, ct) is null
            ? null
            : await db.Recurrences.AsNoTracking().FirstOrDefaultAsync(r => r.ItemId == itemId, ct);
        return recurrence is null ? ApiErrors.NotFound("The event does not repeat.") : TypedResults.Ok(await ResponseAsync(db, recurrence, ct));
    }

    /// <summary>Makes the event repeat (RFC 5545 RRULE) in an IANA time zone (default: the caller's preferred time zone).</summary>
    private static async Task<Results<Ok<RecurrenceResponse>, ValidationProblem, ProblemHttpResult>> SetRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, RecurrenceRequest request, CalendarAccess access, CalendarDbContext db,
        IUserPreferences preferences, ICurrentUser user, CancellationToken ct)
    {
        var rule = request.Rule?.Trim() ?? string.Empty;
        rule = rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? rule[6..] : rule;
        var zone = !string.IsNullOrWhiteSpace(request.TimeZone) ? request.TimeZone.Trim()
            : user.UserId is { } userId ? (await preferences.GetAsync(userId, ct)).TimeZone : "UTC";
        var errors = new Dictionary<string, string[]>();
        if (rule.Length is 0 or > 500 || !IsValidRule(rule))
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

        var item = await access.EventAsync(workspaceId, listId, itemId, ct);
        if (item is null || await db.OccurrenceChanges.AnyAsync(e => e.OverrideItemId == itemId, ct))
        {
            return ApiErrors.NotFound();
        }

        if (item.Access < WorkspaceAccessLevel.Contribute)
        {
            return CalendarAccess.Forbidden();
        }

        var recurrence = await db.Recurrences.FirstOrDefaultAsync(r => r.ItemId == itemId, ct);
        if (recurrence is null)
        {
            recurrence = new EventRecurrence { Id = Ids.New(), ItemId = itemId, WorkspaceId = workspaceId, ListId = listId, Rule = rule, TimeZone = zone };
            db.Recurrences.Add(recurrence);
        }

        recurrence.Rule = rule;
        recurrence.TimeZone = zone;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ResponseAsync(db, recurrence, ct));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveRecurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, CalendarAccess access, CalendarDbContext db, CancellationToken ct)
    {
        var item = await access.EventAsync(workspaceId, listId, itemId, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (item.Access < WorkspaceAccessLevel.Contribute)
        {
            return CalendarAccess.Forbidden();
        }

        await db.Recurrences.Where(r => r.ItemId == itemId).ExecuteDeleteAsync(ct);
        await db.OccurrenceChanges.Where(e => e.MasterItemId == itemId && e.OverrideItemId == null).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Cancels one occurrence of a series (EXDATE).</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> CancelOccurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, DateTimeOffset occurrenceStart, CalendarAccess access, CalendarDbContext db, CancellationToken ct)
    {
        var (series, problem) = await access.OccurrenceAsync(workspaceId, listId, itemId, occurrenceStart, ct);
        if (problem is not null)
        {
            return problem;
        }

        var start = occurrenceStart.ToUniversalTime();
        var exception = await db.OccurrenceChanges.FirstOrDefaultAsync(e => e.MasterItemId == itemId && e.OriginalStart == start, ct);
        if (exception is null)
        {
            db.OccurrenceChanges.Add(new OccurrenceChange { Id = Ids.New(), MasterItemId = series!.Id, OriginalStart = start });
        }
        else
        {
            exception.OverrideItemId = null; // A moved occurrence is cancelled too; its item stays as a single event.
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Moves or changes one occurrence (RECURRENCE-ID): it becomes its own event item with the series'
    /// values and the given changes. Changing it again updates that item.
    /// </summary>
    private static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> ChangeOccurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, DateTimeOffset occurrenceStart, OccurrenceRequest request,
        CalendarAccess access, CalendarDbContext db, IListItemStore items, CancellationToken ct)
    {
        var (series, problem) = await access.OccurrenceAsync(workspaceId, listId, itemId, occurrenceStart, ct);
        if (problem is not null)
        {
            return problem;
        }

        var start = occurrenceStart.ToUniversalTime();
        var exception = await db.OccurrenceChanges.FirstOrDefaultAsync(e => e.MasterItemId == itemId && e.OriginalStart == start, ct);
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
                    db.OccurrenceChanges.Add(new OccurrenceChange { Id = Ids.New(), MasterItemId = itemId, OriginalStart = start, OverrideItemId = result.Item!.Id });
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
            ListItemStatus.Ok => TypedResults.Ok(new JsonObject { ["itemId"] = result.Item!.Id, ["fields"] = result.Item.Fields.DeepClone() }),
            ListItemStatus.Invalid => ApiErrors.Validation(result.Errors!.ToDictionary()),
            ListItemStatus.Forbidden => CalendarAccess.Forbidden(),
            ListItemStatus.NotFound => ApiErrors.NotFound(),
            _ => ApiErrors.Conflict("rejected", result.Message ?? "The change was rejected."),
        };
    }

    private static async Task<RecurrenceResponse> ResponseAsync(CalendarDbContext db, EventRecurrence recurrence, CancellationToken ct)
    {
        var exceptions = await db.OccurrenceChanges.AsNoTracking().Where(e => e.MasterItemId == recurrence.ItemId).OrderBy(e => e.OriginalStart).ToListAsync(ct);
        return new RecurrenceResponse(
            recurrence.Rule,
            recurrence.TimeZone,
            [.. exceptions.Where(e => e.OverrideItemId is null).Select(e => e.OriginalStart)],
            [.. exceptions.Where(e => e.OverrideItemId is not null).Select(e => new OverrideResponse(e.OriginalStart, e.OverrideItemId!.Value))]);
    }

    private static bool IsValidRule(string rule)
    {
        try
        {
            _ = new Ical.Net.DataTypes.RecurrencePattern(rule);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ---- Time ranges ------------------------------------------------------------

    /// <summary>
    /// Events and due tasks between <c>start</c> and <c>end</c> (ISO 8601, at most 366 days) across every
    /// calendar and task list the caller can read (CAL-03), recurring series expanded.
    /// </summary>
    private static async Task<Results<Ok<CalendarResponse>, ValidationProblem>> MyRangeAsync(
        DateTimeOffset? start, DateTimeOffset? end, bool? includeTasks, CalendarService calendar, CancellationToken ct)
    {
        if (RangeProblem(start, end) is { } problem)
        {
            return problem;
        }

        var lists = await calendar.ListsAsync(null, null, ct);
        return TypedResults.Ok(new CalendarResponse(await calendar.RangeAsync(lists, start!.Value, end!.Value, includeTasks ?? true, ct)));
    }

    private static async Task<Results<Ok<CalendarResponse>, ValidationProblem, ProblemHttpResult>> ListRangeAsync(
        Guid workspaceId, Guid listId, DateTimeOffset? start, DateTimeOffset? end, bool? includeTasks, CalendarService calendar, CancellationToken ct)
    {
        if (RangeProblem(start, end) is { } problem)
        {
            return problem;
        }

        var lists = await calendar.ListsAsync(workspaceId, listId, ct);
        return lists.Count == 0
            ? ApiErrors.NotFound("The calendar or task list was not found.")
            : TypedResults.Ok(new CalendarResponse(await calendar.RangeAsync(lists, start!.Value, end!.Value, includeTasks ?? true, ct)));
    }

    private static ValidationProblem? RangeProblem(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null || end <= start || end - start > MaxRange
            ? ApiErrors.Validation(new Dictionary<string, string[]> { ["range"] = ["start and end (ISO 8601) are required; end after start, at most 366 days."] })
            : null;

    // ---- iCalendar ------------------------------------------------------------------

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ExportAsync(
        Guid workspaceId, Guid listId, CalendarService calendar, ICalendarService ical, CancellationToken ct)
    {
        var lists = await calendar.ListsAsync(workspaceId, listId, ct);
        return lists.Count == 0
            ? ApiErrors.NotFound("The calendar or task list was not found.")
            : TypedResults.Text(await ical.ExportAsync(lists, ct), ICalendarService.MediaType, Encoding.UTF8);
    }

    /// <summary>Imports events from an iCalendar body (<c>text/calendar</c>, up to 10 MB) into an event list.</summary>
    private static async Task<Results<Ok<ImportResult>, ValidationProblem, ProblemHttpResult>> ImportAsync(
        Guid workspaceId, Guid listId, HttpRequest http, IListItemStore items, ICalendarService ical, CancellationToken ct)
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

        var result = await ical.ImportAsync(list, new string(buffer, 0, length), ct);
        return result.Created + result.Updated == 0 && result.Errors.Count > 0 && result.Skipped == 0
            ? ApiErrors.Validation(new Dictionary<string, string[]> { ["calendar"] = [.. result.Errors] })
            : TypedResults.Ok(result);
    }

    // ---- Feeds --------------------------------------------------------------------

    private static async Task<Ok<List<FeedResponse>>> ListFeedsAsync(CalendarDbContext db, ICurrentUser user, CancellationToken ct) =>
        TypedResults.Ok((await db.Feeds.AsNoTracking().Where(f => f.UserId == user.UserId).OrderBy(f => f.CreatedAt).ToListAsync(ct))
            .Select(f => new FeedResponse(f.Id, f.Name, f.WorkspaceId, f.ListId, f.CreatedAt, null)).ToList());

    /// <summary>
    /// A read-only subscription URL for calendar apps (CAL-04): one list, or all the caller's calendar and
    /// task lists. The URL contains a secret, is shown once, and acts as the caller with their current access.
    /// </summary>
    private static async Task<Results<Created<FeedResponse>, ValidationProblem, ProblemHttpResult>> CreateFeedAsync(
        FeedRequest request, CalendarDbContext db, CalendarService calendar, ICurrentUser user, ITenantContext tenant, HttpRequest http, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return ApiErrors.Problem(StatusCodes.Status403Forbidden, "userRequired", "Feeds belong to a user.");
        }

        if ((request.ListId is null) != (request.WorkspaceId is null) || (request.Name?.Length ?? 0) > 200)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["listId"] = ["Give both workspaceId and listId, or neither; names have at most 200 characters."] });
        }

        if (request.ListId is not null && (await calendar.ListsAsync(request.WorkspaceId, request.ListId, ct)).Count == 0)
        {
            return ApiErrors.NotFound("The calendar or task list was not found.");
        }

        var secret = Base64UrlSecret();
        var feed = new CalendarFeed
        {
            Id = Ids.New(),
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Calendar" : request.Name.Trim(),
            WorkspaceId = request.WorkspaceId,
            ListId = request.ListId,
            SecretHash = Hash(secret),
        };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync(ct);
        var url = $"{http.Scheme}://{http.Host}{http.PathBase}{ApiRoutes.V1}/calendarFeeds/{tenant.TenantId:N}{secret}.ics";
        return TypedResults.Created($"{ApiRoutes.V1}/me/calendarFeeds/{feed.Id}", new FeedResponse(feed.Id, feed.Name, feed.WorkspaceId, feed.ListId, feed.CreatedAt, url));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteFeedAsync(Guid id, CalendarDbContext db, ICurrentUser user, CancellationToken ct) =>
        await db.Feeds.Where(f => f.Id == id && f.UserId == user.UserId).ExecuteDeleteAsync(ct) == 0 ? ApiErrors.NotFound() : TypedResults.NoContent();

    /// <summary>
    /// The feed (no login: the token identifies tenant and feed). Runs as the feed's owner, so it shows
    /// what they can read now; revoked feeds and deactivated owners answer 404.
    /// </summary>
    private static async Task<Results<ContentHttpResult, NotFound>> FeedAsync(string token, ITenantScopeFactory tenants, CancellationToken ct)
    {
        if (token.Length < 40 || !Guid.TryParseExact(token[..32], "N", out var tenantId))
        {
            return TypedResults.NotFound();
        }

        var hash = Hash(token[32..]);
        if (await tenants.CreateScopeAsync(tenantId, null, ct) is not { } lookup)
        {
            return TypedResults.NotFound();
        }

        CalendarFeed? feed;
        await using (lookup)
        {
            feed = await lookup.ServiceProvider.GetRequiredService<CalendarDbContext>().Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.SecretHash == hash, ct);
            if (feed is null || !await lookup.ServiceProvider.GetRequiredService<IUserDirectory>().IsActiveAsync(feed.UserId, ct))
            {
                return TypedResults.NotFound();
            }
        }

        if (await tenants.CreateScopeAsync(tenantId, feed.UserId, ct) is not { } owner)
        {
            return TypedResults.NotFound();
        }

        await using var scope = owner;
        var calendar = scope.ServiceProvider.GetRequiredService<CalendarService>();
        var lists = await calendar.ListsAsync(feed.WorkspaceId, feed.ListId, ct);
        var text = await scope.ServiceProvider.GetRequiredService<ICalendarService>().ExportAsync(lists, ct);
        return TypedResults.Text(text, ICalendarService.MediaType, Encoding.UTF8);
    }

    private static string Base64UrlSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

/// <summary>Resolves events with the caller's access, via the lists engine.</summary>
internal sealed class CalendarAccess(IListItemStore items, CalendarDbContext db)
{
    public static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    /// <summary>The item when it is readable and its list has the event content type.</summary>
    public async Task<ListItemData?> EventAsync(Guid workspaceId, Guid listId, Guid itemId, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        return list is null || !list.ContentTypeKeys.Contains(CalendarService.EventKey) ? null : await items.GetAsync(workspaceId, listId, itemId, ct);
    }

    /// <summary>The series when <paramref name="occurrenceStart"/> is one of its occurrences and the caller may change it.</summary>
    public async Task<(ListItemData? Series, ProblemHttpResult? Problem)> OccurrenceAsync(
        Guid workspaceId, Guid listId, Guid itemId, DateTimeOffset occurrenceStart, CancellationToken ct)
    {
        var series = await EventAsync(workspaceId, listId, itemId, ct);
        var recurrence = series is null ? null : await db.Recurrences.AsNoTracking().FirstOrDefaultAsync(r => r.ItemId == itemId, ct);
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
