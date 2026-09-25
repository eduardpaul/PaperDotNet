using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notes.Data;

namespace PaperDotNet.Notes.Features;

/// <summary>A note the caller can read.</summary>
public sealed record LinkedNote(Guid WorkspaceId, Guid ListId, Guid ItemId, string Title);

/// <summary>A wiki link of a note; <see cref="Note"/> is the note it points to (null when none exists or the caller cannot read it).</summary>
public sealed record NoteLinkResponse(string Target, string? Heading, string? Alias, bool Embed, LinkedNote? Note);

public sealed record NoteLinksResponse([property: JsonPropertyName("value")] IReadOnlyList<NoteLinkResponse> Value);

public sealed record BacklinksResponse([property: JsonPropertyName("value")] IReadOnlyList<LinkedNote> Value);

/// <summary>Links and backlinks of notes (LST-18). Reading needs Read on the note; linked notes are listed only when readable.</summary>
internal static class NoteEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var item = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}", "Notes");
        item.MapGet("/noteLinks", LinksAsync).RequireScope(NoteScopes.Read).WithName("GetNoteLinks");
        item.MapGet("/backlinks", BacklinksAsync).RequireScope(NoteScopes.Read).WithName("GetNoteBacklinks");
    }

    /// <summary>The note's wiki links in order, resolved to notes where possible.</summary>
    private static async Task<Results<Ok<NoteLinksResponse>, ProblemHttpResult>> LinksAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, NotesDbContext db, CancellationToken ct)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var links = await db.Links.AsNoTracking().Where(l => l.SourceItemId == itemId).OrderBy(l => l.Ordinal).ToListAsync(ct);
        var notes = await VisibleAsync(items, db, links.Where(l => l.TargetItemId != null).Select(l => l.TargetItemId!.Value), ct);
        return TypedResults.Ok(new NoteLinksResponse([.. links.Select(l =>
            new NoteLinkResponse(l.Target, l.Heading, l.Alias, l.Embed, l.TargetItemId is { } target ? notes.GetValueOrDefault(target) : null))]));
    }

    /// <summary>Notes that link to this note, by title.</summary>
    private static async Task<Results<Ok<BacklinksResponse>, ProblemHttpResult>> BacklinksAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, NotesDbContext db, CancellationToken ct)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var sources = await db.Links.AsNoTracking().Where(l => l.TargetItemId == itemId && l.SourceItemId != itemId)
            .Select(l => l.SourceItemId).Distinct().Take(500).ToListAsync(ct);
        var notes = await VisibleAsync(items, db, sources, ct);
        return TypedResults.Ok(new BacklinksResponse([.. notes.Values.OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.ItemId)]));
    }

    /// <summary>The notes among <paramref name="ids"/> the caller can read.</summary>
    private static async Task<Dictionary<Guid, LinkedNote>> VisibleAsync(IListItemStore items, NotesDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var entries = await db.Notes.AsNoTracking().Where(n => wanted.Contains(n.ItemId)).ToListAsync(ct);
        var visible = new Dictionary<Guid, LinkedNote>();
        foreach (var entry in entries)
        {
            if (await items.GetAsync(entry.WorkspaceId, entry.ListId, entry.ItemId, ct) is { } note)
            {
                visible[entry.ItemId] = new LinkedNote(entry.WorkspaceId, entry.ListId, entry.ItemId, NoteTemplates.Text(note.Fields["title"]) ?? entry.Title);
            }
        }

        return visible;
    }
}
