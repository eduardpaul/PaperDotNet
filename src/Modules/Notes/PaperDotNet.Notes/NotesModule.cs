using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
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
/// Notes (LST-18): Markdown notes are list items of the <c>note</c> content type; this module adds #tags as keywords,
/// wiki links with backlinks and link updates on renames. Built on the extension SDK only (EXT-06).
/// </summary>
public sealed class NotesModule : IModule
{
    public string Name => "Notes";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<NotesDbContext>(NotesDbContext.Schema);
        services.AddSingleton(NoteTemplates.ContentType);
        services.AddSingleton(NoteTemplates.List);
        services.AddScoped<IItemMutator, NoteTagsMutator>();
        services.AddEventSubscriber<ItemAdded, NoteLinkIndexer>();
        services.AddEventSubscriber<ItemUpdated, NoteLinkIndexer>();
        services.AddEventSubscriber<ItemRestored, NoteLinkIndexer>();
        services.AddEventSubscriber<ItemDeleted, NoteLinkIndexer>();
        services.AddEventSubscriber<ItemPurged, NoteLinkIndexer>();
        services.AddScopes(NoteScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => NoteEndpoints.Map(endpoints);
}
