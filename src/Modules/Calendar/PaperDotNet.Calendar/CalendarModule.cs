using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
/// Calendar: events are list items of the <c>event</c> content type; this module adds recurrence, time-range queries,
/// iCalendar (Ical.Net) and event reminders.
/// </summary>
public sealed class CalendarModule : IModule
{
    public string Name => "Calendar";

    public IJsonTypeInfoResolver Json => CalendarJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CalendarDbContext>();
        services.AddSingleton(CalendarTemplates.ContentType);
        services.AddSingleton(CalendarTemplates.List);
        services.AddScoped<IItemMutator, EventTimesMutator>();
        services.AddTenantRecurringJob<EventReminderJob>(EventReminderJob.Name, EventReminderJob.Schedule);
        services.AddScopes(CalendarScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => CalendarEndpoints.Map(endpoints);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EventSeriesRequest))]
[JsonSerializable(typeof(EventSeriesResponse))]
[JsonSerializable(typeof(OccurrenceRequest))]
[JsonSerializable(typeof(OccurrenceResponse))]
[JsonSerializable(typeof(CalendarResponse))]
[JsonSerializable(typeof(ImportResult))]
[JsonSerializable(typeof(FeedRequest))]
[JsonSerializable(typeof(FeedResponse))]
[JsonSerializable(typeof(List<FeedResponse>))]
internal sealed partial class CalendarJson : JsonSerializerContext;
