using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notes.Data;
using PaperDotNet.Notes.Features;
using PaperDotNet.Persistence;

using PaperDotNet.Workflows.Contracts;

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
        services.AddWorkflowActivity<NoteLinkIndexer>();
        services.AddWorkflow(ItemChangeWorkflows.Reaction("notes.links", "Update note links", "Updates wiki links and backlinks after a note changes.", "notes.links",
            [WorkflowTriggers.ItemAdded, WorkflowTriggers.ItemUpdated, WorkflowTriggers.ItemDeleted, WorkflowTriggers.ItemRestored], NoteTemplates.ContentTypeKey));
        services.AddModuleDbContext<NotesDbContext>(NotesDbContext.Schema);
        services.AddScoped<IItemMoveParticipant, NotesItemMoveParticipant>();
        services.AddSingleton(NoteTemplates.ContentType);
        services.AddSingleton(NoteTemplates.List);
        services.AddScoped<IItemMutator, NoteTagsMutator>();
        services.AddEventSubscriber<ItemPurged, NoteLinkIndexer>();
        services.AddScopes(NoteScopes.All);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => NoteEndpoints.Map(endpoints);
}
