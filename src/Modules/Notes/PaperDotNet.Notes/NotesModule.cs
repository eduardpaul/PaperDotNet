using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PaperDotNet.Abstractions;
using PaperDotNet.Notes.Data;
using PaperDotNet.Notes.Features;
using PaperDotNet.Persistence;

namespace PaperDotNet.Notes;

public static class NoteScopes
{
    public const string Read = "note.read";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See the links and backlinks of notes.", GrantedToMembers: true),
    ];
}

/// <summary>
/// Notes (LST-18): Markdown notes are list items of the <c>note</c> content type; this module adds wiki links with
/// backlinks and link updates on renames (<see cref="NoteLinkSubscriber"/>). #tags as keywords come with Taxonomy (T12).
/// </summary>
public sealed class NotesModule : IModule
{
    public string Name => "Notes";

    public IJsonTypeInfoResolver Json => NotesJson.Default;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<NotesDbContext>();
        services.AddSingleton(NoteTemplates.ContentType);
        services.AddSingleton(NoteTemplates.List);
        services.AddScopes(NoteScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => NoteEndpoints.Map(endpoints);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(NoteLinksResponse))]
[JsonSerializable(typeof(BacklinksResponse))]
internal sealed partial class NotesJson : JsonSerializerContext;
