using System.Globalization;
using Ical.Net.CalendarComponents;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Lists.Contracts;
using IcalCalendar = Ical.Net.Calendar;

namespace PaperDotNet.Calendar.Features;

internal sealed class CalendarSourceReplication(
    CalendarDbContext db, IListItemStore items, CalendarSourceHttp http, IDataProtectionProvider protection,
    ITenantContext tenant, CalendarImportScope importScope, TimeProvider time)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public IDataProtector Protector => protection.CreateProtector("PaperDotNet.Calendar.Sources", tenant.TenantId!.Value.ToString("N"));

    public async Task<bool> RefreshAsync(Guid sourceId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var leaseId = Ids.New();
        var acquired = await db.Subscriptions.Where(s => s.Id == sourceId && !s.Paused && (s.LeaseUntil == null || s.LeaseUntil <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeaseId, leaseId).SetProperty(p => p.LeaseUntil, now + LeaseDuration), ct);
        if (acquired == 0)
        {
            return false;
        }

        string? error = null;
        var created = 0;
        var updated = 0;
        var removed = 0;
        CalendarDownload? download = null;
        try
        {
            db.ChangeTracker.Clear();
            var source = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sourceId, ct);
            var list = await items.AsSystem().GetListAsync(source.WorkspaceId, source.ListId, ct);
            if (list is null || !list.ContentTypeKeys.Contains(CalendarService.EventKey))
            {
                throw new CalendarSourceException("The destination no longer supports calendar events.");
            }

            download = await http.DownloadAsync(Protector.Unprotect(source.ProtectedUrl), source.HttpETag, source.HttpLastModified, ct);
            if (download.Text is not null)
            {
                var events = ParseSnapshot(download.Text);
                var importer = new ICalendarService(items.AsSystem(), db);
                importScope.Active = true;
                var sourceItems = await db.Sources.Where(s => s.SubscriptionId == sourceId).ToListAsync(ct);
                var activeKeys = events.Select(EventKey).ToHashSet(StringComparer.Ordinal);

                foreach (var vevent in events.OrderBy(e => e.RecurrenceIdentifier is null ? 0 : 1))
                {
                    await RenewAsync(sourceId, leaseId, ct);
                    // Replace the master's exception set. Old override items are removed below only after a complete import.
                    if (vevent.RecurrenceIdentifier is null && sourceItems.FirstOrDefault(s => s.Uid == vevent.Uid) is { } master)
                    {
                        await db.OccurrenceChanges.Where(e => e.MasterItemId == master.ItemId).ExecuteDeleteAsync(ct);
                        foreach (var tracked in db.ChangeTracker.Entries<OccurrenceChange>().Where(e => e.Entity.MasterItemId == master.ItemId).ToList())
                        {
                            tracked.State = EntityState.Detached;
                        }
                    }

                    var result = await importer.ImportEventAsync(list, vevent, ct, sourceId);
                    if (result is null)
                    {
                        throw new CalendarSourceException("A source event could not be imported. Check its dates and the list's required fields; no absent events were removed.");
                    }

                    if (result.Value) { created++; } else { updated++; }
                }

                foreach (var absent in sourceItems.Where(s => !activeKeys.Contains(s.Uid)))
                {
                    await RenewAsync(sourceId, leaseId, ct);
                    var item = await items.AsSystem().GetAsync(list.WorkspaceId, list.Id, absent.ItemId, ct);
                    if (item is not null)
                    {
                        var result = await items.AsSystem().DeleteAsync(list.WorkspaceId, list.Id, item.Id, item.Version, ct);
                        if (!result.Succeeded)
                        {
                            throw new CalendarSourceException("An absent source event could not be removed. Refresh again to retry.");
                        }

                        removed++;
                    }

                    await db.Recurrences.Where(r => r.ItemId == absent.ItemId).ExecuteDeleteAsync(ct);
                    await db.OccurrenceChanges.Where(r => r.MasterItemId == absent.ItemId || r.OverrideItemId == absent.ItemId).ExecuteDeleteAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // HTTP/parser/crypto exception messages may include the secret URL or raw feed values.
            error = ex is CalendarSourceException ? ex.Message : "The calendar source could not refresh. Check its URL and try again.";
        }
        finally
        {
            importScope.Active = false;
            db.ChangeTracker.Clear();
            var finish = time.GetUtcNow();
            if (!ct.IsCancellationRequested)
            {
                if (error is null)
                {
                    await db.Subscriptions.Where(s => s.Id == sourceId && s.LeaseId == leaseId)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSuccess, finish).SetProperty(p => p.Error, (string?)null)
                            .SetProperty(p => p.Created, created).SetProperty(p => p.Updated, updated).SetProperty(p => p.Removed, removed)
                            .SetProperty(p => p.HttpETag, download!.ETag).SetProperty(p => p.HttpLastModified, download!.LastModified)
                            .SetProperty(p => p.LeaseId, (Guid?)null).SetProperty(p => p.LeaseUntil, (DateTimeOffset?)null)
                            .SetProperty(p => p.Version, p => p.Version + 1).SetProperty(p => p.UpdatedAt, finish), ct);
                }
                else
                {
                    await db.Subscriptions.Where(s => s.Id == sourceId && s.LeaseId == leaseId)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Error, error).SetProperty(p => p.LeaseId, (Guid?)null)
                            .SetProperty(p => p.LeaseUntil, (DateTimeOffset?)null).SetProperty(p => p.Version, p => p.Version + 1)
                            .SetProperty(p => p.UpdatedAt, finish), ct);
                }
            }
        }

        return error is null;
    }

    private async Task RenewAsync(Guid sourceId, Guid leaseId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var renewed = await db.Subscriptions.Where(s => s.Id == sourceId && s.LeaseId == leaseId && s.LeaseUntil > now)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeaseUntil, now + LeaseDuration), ct);
        if (renewed == 0)
        {
            throw new CalendarSourceException("The source refresh lease expired. Refresh again to resume.");
        }
    }

    internal static string EventKey(CalendarEvent e) => e.RecurrenceIdentifier?.StartTime is { } original
        ? $"{e.Uid}#{ICalendarService.Instant(original).ToString("O", CultureInfo.InvariantCulture)}" : e.Uid!;

    internal static IReadOnlyList<CalendarEvent> ParseSnapshot(string text)
    {
        var normalized = text.Trim();
        if (!normalized.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new CalendarSourceException("The source is not a complete iCalendar document.");
        }

        IcalCalendar calendar;
        try
        {
            calendar = IcalCalendar.Load(normalized) ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            throw new CalendarSourceException("The source is not a valid iCalendar document.");
        }

        var events = calendar.Events.ToList();
        // Detect truncated or silently dropped components, including extra calendars in the response.
        var lines = normalized.ReplaceLineEndings("\n").Split('\n');
        if (lines.Count(l => l.TrimEnd().Equals("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) != 1
            || lines.Count(l => l.TrimEnd().Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) != events.Count
            || lines.Count(l => l.TrimEnd().Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase)) != events.Count
            || events.Count > ICalendarService.MaxImportedComponents)
        {
            throw new CalendarSourceException("The source contains incomplete events or exceeds the 2000-event limit. Existing events were retained.");
        }

        if (events.Any(e => string.IsNullOrWhiteSpace(e.Uid) || e.Uid.Length > 200)
            || events.GroupBy(EventKey, StringComparer.Ordinal).Any(g => g.Count() != 1))
        {
            throw new CalendarSourceException("Every source event must have a unique UID and occurrence identity.");
        }

        if (lines.Any(l => l.StartsWith("EXRULE:", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CalendarSourceException("EXRULE recurrences are unsupported. Existing events were retained.");
        }

        var masters = events.Where(e => e.RecurrenceIdentifier is null).ToDictionary(e => e.Uid!, StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Status == "CANCELLED")
            {
                if (e.RecurrenceIdentifier?.StartTime is { } original && masters.TryGetValue(e.Uid!, out var master))
                {
                    master.ExceptionDates.Add(original);
                }

                continue;
            }

            if (e.DtStart is null || e.RecurrenceDates.GetAllDates().Any()
                || (e.DtStart.TzId is { } zone && !CalendarService.IsKnownTimeZone(zone))
                || (e.DtEnd is { } end && ICalendarService.Instant(end) < ICalendarService.Instant(e.DtStart))
                || (e.RecurrenceIdentifier is not null && (!masters.TryGetValue(e.Uid!, out var series) || series.RecurrenceRule is null)))
            {
                throw new CalendarSourceException("The source contains an unsupported event, time zone or recurrence. Existing events were retained.");
            }
        }

        return events.Where(e => e.Status != "CANCELLED"
            && (!masters.TryGetValue(e.Uid!, out var master) || master.Status != "CANCELLED")).ToList();
    }
}
