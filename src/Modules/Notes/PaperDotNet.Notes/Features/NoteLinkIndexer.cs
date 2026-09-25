using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notes.Data;

namespace PaperDotNet.Notes.Features;

/// <summary>
/// Keeps note titles and wiki links up to date (LST-18), in the background:
/// <list type="bullet">
/// <item>a saved note's links are parsed again and resolved to the notes of the workspace with that title;</item>
/// <item>links waiting for a title are resolved when a note gets it;</item>
/// <item>when a note is renamed, the notes that link to it are rewritten to the new title (as the organization);</item>
/// <item>a deleted note's links go, and links to it wait for a note with its title again.</item>
/// </list>
/// Idempotent: every run recomputes the note's state from the item. Each run replaces the note's links in one
/// transaction, so readers never see a note without its links while it is indexed again.
/// </summary>
internal sealed class NoteLinkIndexer(NotesDbContext db, IListItemStore items, EventCausation causation)
    : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>, IEventSubscriber<ItemRestored>, IEventSubscriber<ItemDeleted>, IEventSubscriber<ItemPurged>
{
    /// <summary>Renames made by automatic changes stop rewriting links at this depth (loop protection).</summary>
    private const int MaxRewriteDepth = 3;

    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken) => IndexAsync(integrationEvent, cancellationToken);

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken) => IndexAsync(integrationEvent, cancellationToken);

    public Task HandleAsync(ItemRestored integrationEvent, CancellationToken cancellationToken) => IndexAsync(integrationEvent, cancellationToken);

    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) => RemoveAsync(integrationEvent.ItemId, cancellationToken);

    public Task HandleAsync(ItemPurged integrationEvent, CancellationToken cancellationToken) => RemoveAsync(integrationEvent.ItemId, cancellationToken);

    private async Task IndexAsync(ItemEvent integrationEvent, CancellationToken ct)
    {
        if (integrationEvent.IsFolder)
        {
            return;
        }

        var store = items.AsSystem();
        var item = await store.GetAsync(integrationEvent.WorkspaceId, integrationEvent.ListId, integrationEvent.ItemId, ct);
        var list = item is null ? null : await store.GetListAsync(item.WorkspaceId, item.ListId, ct);
        if (item is null || list?.ContentTypes.FirstOrDefault(c => c.Id == item.ContentTypeId)?.Key != NoteTemplates.ContentTypeKey)
        {
            // Gone, or no longer a note (content type changed).
            await RemoveAsync(integrationEvent.ItemId, ct);
            return;
        }

        var title = NoteTemplates.Text(item.Fields["title"]) ?? string.Empty;
        var normalized = NoteMarkdown.Normalize(title);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entry = await db.Notes.FirstOrDefaultAsync(n => n.ItemId == item.Id, ct);
        (string Title, string NormalizedTitle)? previous = entry is null ? null : (entry.Title, entry.NormalizedTitle);
        if (entry is null)
        {
            entry = new NoteEntry { ItemId = item.Id, Title = title, NormalizedTitle = normalized };
            db.Notes.Add(entry);
        }

        entry.WorkspaceId = item.WorkspaceId;
        entry.ListId = item.ListId;
        entry.Title = title;
        entry.NormalizedTitle = normalized;
        await db.SaveChangesAsync(ct);

        // The note's own links, resolved to notes of the workspace (the oldest note wins when titles repeat).
        await db.Links.Where(l => l.SourceItemId == item.Id).ExecuteDeleteAsync(ct);
        var links = NoteMarkdown.Links(NoteTemplates.Text(item.Fields[NoteTemplates.BodyField]) ?? string.Empty);
        var targets = links.Select(l => NoteMarkdown.Normalize(l.Target)).Distinct().ToList();
        var notes = (await db.Notes.AsNoTracking()
                .Where(n => n.WorkspaceId == item.WorkspaceId && targets.Contains(n.NormalizedTitle))
                .Select(n => new { n.NormalizedTitle, n.ItemId })
                .ToListAsync(ct))
            .GroupBy(n => n.NormalizedTitle)
            .ToDictionary(g => g.Key, g => g.Min(n => n.ItemId));
        db.Links.AddRange(links.Select((l, i) => new NoteLink
        {
            Id = Ids.New(),
            WorkspaceId = item.WorkspaceId,
            SourceItemId = item.Id,
            SourceListId = item.ListId,
            Ordinal = i,
            Target = Truncate(l.Target),
            NormalizedTarget = Truncate(NoteMarkdown.Normalize(l.Target)),
            Heading = l.Heading is null ? null : Truncate(l.Heading),
            Alias = l.Alias is null ? null : Truncate(l.Alias),
            Embed = l.Embed,
            TargetItemId = notes.TryGetValue(NoteMarkdown.Normalize(l.Target), out var target) ? target : null,
        }));
        await db.SaveChangesAsync(ct);

        if (previous?.NormalizedTitle != normalized)
        {
            // A new title: links waiting for it now point here.
            await db.Links.Where(l => l.WorkspaceId == item.WorkspaceId && l.TargetItemId == null && l.NormalizedTarget == normalized)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.TargetItemId, item.Id), ct);
        }

        await transaction.CommitAsync(ct);
        if (previous?.NormalizedTitle == normalized)
        {
            return;
        }

        if (previous is { } old && integrationEvent.Depth < MaxRewriteDepth)
        {
            await RewriteLinksAsync(item.WorkspaceId, item.Id, old.Title, old.NormalizedTitle, title, integrationEvent.Depth, ct);
        }
    }

    /// <summary>Renamed: the notes linking to the old title of this note link to the new one.</summary>
    private async Task RewriteLinksAsync(Guid workspaceId, Guid itemId, string oldTitle, string oldNormalized, string newTitle, int depth, CancellationToken ct)
    {
        var sources = await db.Links.AsNoTracking()
            .Where(l => l.TargetItemId == itemId && l.NormalizedTarget == oldNormalized)
            .Select(l => new { l.SourceItemId, l.SourceListId })
            .Distinct()
            .ToListAsync(ct);
        causation.Depth = depth + 1;
        var store = items.AsSystem();
        foreach (var source in sources)
        {
            var note = await store.GetAsync(workspaceId, source.SourceListId, source.SourceItemId, ct);
            var body = NoteTemplates.Text(note?.Fields[NoteTemplates.BodyField]);
            if (note is null || body is null)
            {
                continue;
            }

            var renamed = NoteMarkdown.RenameLinks(body, oldTitle, newTitle);
            if (renamed != body)
            {
                // The source is indexed again by its own update event.
                await store.UpdateAsync(workspaceId, source.SourceListId, source.SourceItemId, new JsonObject { [NoteTemplates.BodyField] = renamed }, null, ct);
            }
        }
    }

    private async Task RemoveAsync(Guid itemId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Links.Where(l => l.SourceItemId == itemId).ExecuteDeleteAsync(ct);
        await db.Links.Where(l => l.TargetItemId == itemId).ExecuteUpdateAsync(u => u.SetProperty(l => l.TargetItemId, (Guid?)null), ct);
        await db.Notes.Where(n => n.ItemId == itemId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static string Truncate(string text) => text.Length > 1024 ? text[..1024] : text;
}
