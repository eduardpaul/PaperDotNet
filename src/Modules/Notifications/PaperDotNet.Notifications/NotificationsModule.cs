using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notifications.Contracts;
using PaperDotNet.Notifications.Data;
using PaperDotNet.Notifications.Features;
using PaperDotNet.Persistence;

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
/// Notifications (phase 4c): inbox, preferences, follows with digests, webhook channel. Other modules
/// and extensions send through <see cref="INotificationSender"/>. Built on the extension SDK only (EXT-06).
/// </summary>
public sealed class NotificationsModule : IModule
{
    public string Name => "Notifications";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.Schema);
        services.AddOptions<NotificationsOptions>().BindConfiguration(NotificationsOptions.Section);
        services.AddScoped<INotificationSender, NotificationSender>();
        services.AddKeyedSingleton<HttpMessageHandler>(WebhookDispatcher.HandlerKey, (sp, _) => WebhookNetwork.CreateHandler(sp.GetRequiredService<IOptions<NotificationsOptions>>()));
        services.AddSingleton(sp => new WebhookHttp(new HttpClient(sp.GetRequiredKeyedService<HttpMessageHandler>(WebhookDispatcher.HandlerKey), disposeHandler: false)
        {
            Timeout = sp.GetRequiredService<IOptions<NotificationsOptions>>().Value.WebhookTimeout,
        }));
        services.AddTenantRecurringJob<WebhookDispatcher>(WebhookDispatcher.Name, WebhookDispatcher.Schedule);
        services.AddTenantRecurringJob<DigestJob>(DigestJob.Name, DigestJob.Schedule);
        services.AddScoped<AlertSubscriber>();
        services.AddScoped<IEventSubscriber<ItemAdded>>(sp => sp.GetRequiredService<AlertSubscriber>());
        services.AddScoped<IEventSubscriber<ItemUpdated>>(sp => sp.GetRequiredService<AlertSubscriber>());
        services.AddScoped<IEventSubscriber<ItemDeleted>>(sp => sp.GetRequiredService<AlertSubscriber>());
        services.AddScoped<IEventSubscriber<ItemPurged>, PurgedFollows>();
        services.AddScoped<ChangeNotifier>();
        services.AddScoped<IEventSubscriber<ItemAdded>>(sp => sp.GetRequiredService<ChangeNotifier>());
        services.AddScoped<IEventSubscriber<ItemUpdated>>(sp => sp.GetRequiredService<ChangeNotifier>());
        services.AddScoped<IEventSubscriber<ItemDeleted>>(sp => sp.GetRequiredService<ChangeNotifier>());
        services.AddTenantRecurringJob<ChangeDispatcher>(ChangeDispatcher.Name, ChangeDispatcher.Schedule);
        services.AddTenantRecurringJob<ChangeSubscriptionCleanupJob>(ChangeSubscriptionCleanupJob.Name, ChangeSubscriptionCleanupJob.Schedule);
        services.AddScopes(NotificationScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        NotificationEndpoints.Map(endpoints);
        ChangeNotifications.Map(endpoints);
    }
}
