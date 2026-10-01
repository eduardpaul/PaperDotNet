using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notes.Data;

namespace PaperDotNet.Notes.Features;

/// <summary>A note the caller can read.</summary>
public sealed record LinkedNote(Guid WorkspaceId, Guid ListId, Guid ItemId, string Title);

/// <summary>A wiki link of a note; <see cref="Note"/> is the note it points to (left out when none exists or the caller cannot read it).</summary>
public sealed record NoteLinkResponse(
    string Target, string? Heading, string? Alias, bool Embed, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LinkedNote? Note);

public sealed record NoteLinksResponse([property: JsonPropertyName("value")] IReadOnlyList<NoteLinkResponse> Value);

public sealed record BacklinksResponse([property: JsonPropertyName("value")] IReadOnlyList<LinkedNote> Value);

/// <summary>Links and backlinks of notes (LST-18). Reading needs Read on the note; linked notes are listed only when readable.</summary>
internal static class NoteEndpoints
{
    private const int MaxBacklinks = 500;

    public static void Map(IEndpointRouteBuilder app)
    {
        var item = app.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}").WithTags("Notes");
        item.MapGet("/noteLinks", LinksAsync).RequireScope(NoteScopes.Read).WithName("GetNoteLinks")
            .WithDescription("The note's wiki links in order, resolved to notes where possible.");
        item.MapGet("/backlinks", BacklinksAsync).RequireScope(NoteScopes.Read).WithName("GetNoteBacklinks")
            .WithDescription("Notes that link to this note, by title.");
    }

    private static async Task<Results<Ok<NoteLinksResponse>, ProblemHttpResult>> LinksAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, NotesDbContext database, CancellationToken cancellationToken)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var db = database;
        var tenant = caller.TenantId;
        var source = itemId;
        var ct = cancellationToken;
        var links = await db.Links.AsNoTracking().Where(l => l.TenantId == tenant && l.SourceItemId == source).OrderBy(l => l.Ordinal).ToListAsync(ct);
        var notes = await VisibleAsync(items, db, tenant, links.Where(l => l.TargetItemId != null).Select(l => l.TargetItemId!.Value), ct);
        return TypedResults.Ok(new NoteLinksResponse([.. links.Select(l =>
            new NoteLinkResponse(l.Target, l.Heading, l.Alias, l.Embed, l.TargetItemId is { } target ? notes.GetValueOrDefault(target) : null))]));
    }

    private static async Task<Results<Ok<BacklinksResponse>, ProblemHttpResult>> BacklinksAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, NotesDbContext database, CancellationToken cancellationToken)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var db = database;
        var tenant = caller.TenantId;
        var target = itemId;
        var limit = MaxBacklinks * 4; // Constants are copied too: precompiled queries see locals only (ADR-0039).
        var ct = cancellationToken;
        var sources = (await db.Links.AsNoTracking().Where(l => l.TenantId == tenant && l.TargetItemId == target && l.SourceItemId != target)
                .OrderBy(l => l.Id).Take(limit).ToListAsync(ct))
            .Select(l => l.SourceItemId).Distinct().Take(MaxBacklinks);
        var notes = await VisibleAsync(items, db, tenant, sources, ct);
        return TypedResults.Ok(new BacklinksResponse([.. notes.Values.OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.ItemId)]));
    }

    /// <summary>The notes among <paramref name="ids"/> the caller can read.</summary>
    private static async Task<Dictionary<Guid, LinkedNote>> VisibleAsync(IListItemStore items, NotesDbContext database, Guid tenantId, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var ct = cancellationToken;
        var visible = new Dictionary<Guid, LinkedNote>();
        foreach (var id in ids.Distinct())
        {
            var noteId = id;
            var entry = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.TenantId == tenant && n.ItemId == noteId, ct);
            if (entry is not null && await items.GetAsync(entry.WorkspaceId, entry.ListId, entry.ItemId, ct) is { } note)
            {
                visible[entry.ItemId] = new LinkedNote(entry.WorkspaceId, entry.ListId, entry.ItemId, NoteTemplates.Text(note.Fields["title"]) ?? entry.Title);
            }
        }

        return visible;
    }
}
