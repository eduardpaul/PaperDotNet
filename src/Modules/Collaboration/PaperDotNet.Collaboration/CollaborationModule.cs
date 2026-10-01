using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
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
/// Collaboration: comments with @mentions and the activity timeline of items (LST-17). Others add timeline entries with
/// <see cref="IItemActivity"/>. Comments in search come with Search (T12), the <c>comment.added</c> workflow trigger with
/// workflow parity (T14).
/// </summary>
public sealed class CollaborationModule : IModule
{
    public string Name => "Collaboration";

    public IJsonTypeInfoResolver Json => CollaborationJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CollaborationDbContext>();
        services.AddScoped<IItemActivity>(sp => new ItemActivity(sp.GetRequiredService<CollaborationDbContext>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IItemSearchContributor>(sp => new CommentSearchContent(sp.GetRequiredService<CollaborationDbContext>()));
        services.AddScopes(CollaborationScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => CommentEndpoints.Map(endpoints);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CommentRequest))]
[JsonSerializable(typeof(CommentUpdateRequest))]
[JsonSerializable(typeof(CommentResponse))]
[JsonSerializable(typeof(Page<CommentResponse>))]
[JsonSerializable(typeof(Page<ActivityResponse>))]
[JsonSerializable(typeof(List<Guid>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class CollaborationJson : JsonSerializerContext;
