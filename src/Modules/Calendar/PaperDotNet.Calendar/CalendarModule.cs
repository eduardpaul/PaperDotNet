using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Calendar.Features;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;

namespace PaperDotNet.Calendar;

public static class CalendarScopes
{
    public const string Read = "calendar.read";
    public const string Write = "calendar.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See calendars, export them and manage your calendar feeds.", GrantedToMembers: true),
        new(Write, "Change recurrences, import calendars and create calendar feeds.", GrantedToMembers: true),
    ];
}

/// <summary>
/// Calendar (phase 4b). Events are list items of the <c>event</c> content type; this module adds
/// recurrence, time-range queries and iCalendar. Built on the extension SDK only (EXT-06).
/// </summary>
public sealed class CalendarModule : IModule
{
    public string Name => "Calendar";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CalendarDbContext>(CalendarDbContext.Schema);
        services.AddSingleton(CalendarTemplates.ContentType);
        services.AddSingleton(CalendarTemplates.List);
        services.AddScoped<CalendarService>();
        services.AddScoped<ICalendarService>();
        services.AddScoped<CalendarAccess>();
        services.AddScoped<IItemMutator, EventTimesMutator>();
        services.AddScoped<IEventSubscriber<ItemPurged>, PurgedEventData>();
        services.AddTenantRecurringJob<EventReminderJob>(EventReminderJob.Name, EventReminderJob.Schedule);
        services.AddScopes(CalendarScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => CalendarEndpoints.Map(endpoints);
}

/// <summary>Removes the recurrence, exceptions and import sources of permanently deleted events.</summary>
internal sealed class PurgedEventData(CalendarDbContext db) : IEventSubscriber<ItemPurged>
{
    public async Task HandleAsync(ItemPurged integrationEvent, CancellationToken cancellationToken)
    {
        var id = integrationEvent.ItemId;
        await db.Recurrences.Where(r => r.ItemId == id).ExecuteDeleteAsync(cancellationToken);
        await db.OccurrenceChanges.Where(e => e.MasterItemId == id).ExecuteDeleteAsync(cancellationToken);
        await db.OccurrenceChanges.Where(e => e.OverrideItemId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.OverrideItemId, (Guid?)null), cancellationToken);
        await db.Sources.Where(s => s.ItemId == id).ExecuteDeleteAsync(cancellationToken);
    }
}
