using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Collaboration.Contracts;
using PaperDotNet.Collaboration.Data;
using PaperDotNet.Collaboration.Features;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;

namespace PaperDotNet.Collaboration;

public static class CollaborationScopes
{
    public const string Read = "comment.read";
    public const string Write = "comment.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Read comments and the activity of items you can read.", GrantedToMembers: true),
        new(Write, "Comment on items and change your comments.", GrantedToMembers: true),
    ];
}

/// <summary>
/// Collaboration (phase 5d): comments with @mentions and the activity timeline of items (LST-17).
/// Built on the extension SDK only (EXT-06); others add timeline entries with <see cref="IItemActivity"/>.
/// </summary>
public sealed class CollaborationModule : IModule
{
    public string Name => "Collaboration";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CollaborationDbContext>(CollaborationDbContext.Schema);
        services.AddScoped<ItemActivity>();
        services.AddScoped<IItemActivity>(sp => sp.GetRequiredService<ItemActivity>());
        services.AddScoped<CommentMentions>();
        services.AddScoped<IItemSearchContributor, CommentSearchContent>();
        services.AddScoped<ItemActivityRecorder>();
        services.AddScoped<IEventSubscriber<ItemAdded>>(sp => sp.GetRequiredService<ItemActivityRecorder>());
        services.AddScoped<IEventSubscriber<ItemUpdated>>(sp => sp.GetRequiredService<ItemActivityRecorder>());
        services.AddScoped<IEventSubscriber<ItemDeleted>>(sp => sp.GetRequiredService<ItemActivityRecorder>());
        services.AddScoped<IEventSubscriber<ItemRestored>>(sp => sp.GetRequiredService<ItemActivityRecorder>());
        services.AddScoped<IEventSubscriber<ItemPurged>>(sp => sp.GetRequiredService<ItemActivityRecorder>());
        services.AddScopes(CollaborationScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => CommentEndpoints.Map(endpoints);
}
