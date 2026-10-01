using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Notifications.Data;
using PaperDotNet.Notifications.Features;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Notifications;

public static class NotificationScopes
{
    public const string Read = "notification.read";
    public const string Write = "notification.write";
    public const string ChangeSubscriptions = "changeSubscription.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Read your notifications, settings and follows.", GrantedToMembers: true),
        new(Write, "Mark notifications read, change your settings and follow lists or items.", GrantedToMembers: true),
        new(ChangeSubscriptions, "Subscribe to changes of lists and items you can read (signed HTTP notifications).", GrantedToMembers: true),
    ];
}

/// <summary>
/// Notifications: inbox, settings, follows with digests, webhook channel, change subscriptions (API-06) and the
/// <c>notify</c> activity. Other modules and extensions send through <see cref="INotificationSender"/>.
/// </summary>
public sealed class NotificationsModule : IModule
{
    public string Name => "Notifications";

    public IJsonTypeInfoResolver Json => NotificationsJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<NotificationsDbContext>();
        services.Configure<NotificationsOptions>(configuration.GetSection(NotificationsOptions.Section));
        // Factories: Wolverine's generated handlers resolve these by service location (internal types).
        services.AddScoped<INotificationSender>(sp => new NotificationSender(
            sp.GetRequiredService<NotificationsDbContext>(), sp.GetRequiredService<ILiveEvents>(), sp.GetRequiredService<IUserPreferences>(), sp.GetRequiredService<TimeProvider>()));
        services.AddKeyedSingleton<HttpMessageHandler>(WebhookDispatcher.HandlerKey, (sp, _) => WebhookNetwork.CreateHandler(sp.GetRequiredService<IOptions<NotificationsOptions>>()));
        services.AddSingleton(sp => new WebhookHttp(new HttpClient(sp.GetRequiredKeyedService<HttpMessageHandler>(WebhookDispatcher.HandlerKey), disposeHandler: false)
        {
            Timeout = sp.GetRequiredService<IOptions<NotificationsOptions>>().Value.WebhookTimeout,
        }));
        services.AddTenantRecurringJob<WebhookDispatcher>(WebhookDispatcher.Name, WebhookDispatcher.Schedule);
        services.AddTenantRecurringJob<DigestJob>(DigestJob.Name, DigestJob.Schedule);
        services.AddTenantRecurringJob<ChangeDispatcher>(ChangeDispatcher.Name, ChangeDispatcher.Schedule);
        services.AddTenantRecurringJob<ChangeSubscriptionCleanupJob>(ChangeSubscriptionCleanupJob.Name, ChangeSubscriptionCleanupJob.Schedule);
        services.AddWorkflowActivity<NotifyActivity>();
        services.AddScopes(NotificationScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        NotificationEndpoints.Map(endpoints);
        ChangeNotifications.Map(endpoints);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(NotificationResponse))]
[JsonSerializable(typeof(Page<NotificationResponse>))]
[JsonSerializable(typeof(UnreadCount))]
[JsonSerializable(typeof(SettingsRequest))]
[JsonSerializable(typeof(SettingsResponse))]
[JsonSerializable(typeof(Dictionary<string, ChannelChoice>))]
[JsonSerializable(typeof(FollowRequest))]
[JsonSerializable(typeof(FollowResponse))]
[JsonSerializable(typeof(List<FollowResponse>))]
[JsonSerializable(typeof(WebhookBody))]
[JsonSerializable(typeof(ChangeSubscriptionRequest))]
[JsonSerializable(typeof(ChangeSubscriptionUpdate))]
[JsonSerializable(typeof(ChangeSubscriptionResponse))]
[JsonSerializable(typeof(Page<ChangeSubscriptionResponse>))]
[JsonSerializable(typeof(ChangeNotificationBody))]
internal sealed partial class NotificationsJson : JsonSerializerContext;
