using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Notes.Data;

namespace PaperDotNet.Notes.Features;

/// <summary>
/// Keeps note titles and wiki links up to date (LST-18), in the background (a Wolverine handler generated ahead of time):
/// <list type="bullet">
/// <item>a saved note's links are parsed again and resolved to the notes of the workspace with that title;</item>
/// <item>links waiting for a title are resolved when a note gets it;</item>
/// <item>when a note is renamed, the notes that link to it are rewritten to the new title (as the organization);</item>
/// <item>a deleted note's links go, and links to it wait for a note with its title again.</item>
/// </list>
/// Idempotent: every run recomputes the note's state from the item. Each run replaces the note's links in one
/// transaction, so readers never see a note without its links while it is indexed again.
/// </summary>
public static class NoteLinkSubscriber
{
    /// <summary>Renames made by automatic changes stop rewriting links at this depth (loop protection).</summary>
    private const int MaxRewriteDepth = 3;

    public static Task Handle(ItemAdded e, NotesDbContext db, IListItemStore items, CancellationToken cancellationToken) => IndexAsync(e, db, items, cancellationToken);

    public static Task Handle(ItemUpdated e, NotesDbContext db, IListItemStore items, CancellationToken cancellationToken) => IndexAsync(e, db, items, cancellationToken);

    public static Task Handle(ItemRestored e, NotesDbContext db, IListItemStore items, CancellationToken cancellationToken) => IndexAsync(e, db, items, cancellationToken);

    public static Task Handle(ItemDeleted e, NotesDbContext db, CancellationToken cancellationToken) => RemoveAsync(db, e.TenantId, e.ItemId, cancellationToken);

    public static Task Handle(ItemPurged e, NotesDbContext db, CancellationToken cancellationToken) => RemoveAsync(db, e.TenantId, e.ItemId, cancellationToken);

    private static async Task IndexAsync(ItemEvent change, NotesDbContext database, IListItemStore items, CancellationToken cancellationToken)
    {
        if (change.IsFolder)
        {
            return;
        }

        var tenant = change.TenantId;
        var ct = cancellationToken;
        var store = items.AsSystem(new ChangeActor(tenant, null, change.Depth));
        var item = await store.GetAsync(change.WorkspaceId, change.ListId, change.ItemId, ct);
        var list = item is null ? null : await store.GetListAsync(item.WorkspaceId, item.ListId, ct);
        if (item is null || list?.ContentTypes.FirstOrDefault(c => c.Id == item.ContentTypeId)?.Key != NoteTemplates.ContentTypeKey)
        {
            // Gone, or no longer a note (content type changed).
            await RemoveAsync(database, tenant, change.ItemId, ct);
            return;
        }

        var db = database;
        var itemId = item.Id;
        var workspaceId = item.WorkspaceId;
        var title = NoteTemplates.Text(item.Fields["title"]) ?? string.Empty;
        var normalized = NoteMarkdown.Normalize(title);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entry = await db.Notes.FirstOrDefaultAsync(n => n.TenantId == tenant && n.ItemId == itemId, ct);
        (string Title, string NormalizedTitle)? previous = entry is null ? null : (entry.Title, entry.NormalizedTitle);
        if (entry is null)
        {
            entry = new NoteEntry { ItemId = itemId, TenantId = tenant };
            db.Notes.Add(entry);
        }

        entry.WorkspaceId = workspaceId;
        entry.ListId = item.ListId;
        entry.Title = Truncate(title);
        entry.NormalizedTitle = Truncate(normalized);

        // The note's own links, resolved to notes of the workspace (the oldest note wins when titles repeat).
        db.Links.RemoveRange(await db.Links.Where(l => l.TenantId == tenant && l.SourceItemId == itemId).ToListAsync(ct));
        var links = NoteMarkdown.Links(NoteTemplates.Text(item.Fields[NoteTemplates.BodyField]) ?? string.Empty);
        var targets = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var target in links.Select(l => Truncate(NoteMarkdown.Normalize(l.Target))).Distinct())
        {
            var key = target;
            targets[key] = key == normalized
                ? itemId
                : (await db.Notes.AsNoTracking().Where(n => n.TenantId == tenant && n.WorkspaceId == workspaceId && n.NormalizedTitle == key)
                    .OrderBy(n => n.ItemId).Select(n => n.ItemId).Take(1).ToListAsync(ct)).Cast<Guid?>().FirstOrDefault();
        }

        db.Links.AddRange(links.Select((l, i) => new NoteLink
        {
            Id = Ids.New(),
            TenantId = tenant,
            WorkspaceId = workspaceId,
            SourceItemId = itemId,
            SourceListId = item.ListId,
            Ordinal = i,
            Target = Truncate(l.Target),
            NormalizedTarget = Truncate(NoteMarkdown.Normalize(l.Target)),
            Heading = l.Heading is null ? null : Truncate(l.Heading),
            Alias = l.Alias is null ? null : Truncate(l.Alias),
            Embed = l.Embed,
            TargetItemId = targets[Truncate(NoteMarkdown.Normalize(l.Target))],
        }));

        if (previous?.NormalizedTitle != entry.NormalizedTitle)
        {
            // A new title: links waiting for it now point here.
            var title2 = entry.NormalizedTitle;
            foreach (var waiting in await db.Links.Where(l => l.TenantId == tenant && l.WorkspaceId == workspaceId && l.TargetItemId == null && l.NormalizedTarget == title2).ToListAsync(ct))
            {
                waiting.TargetItemId = itemId;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (previous is { } old && old.NormalizedTitle != entry.NormalizedTitle && change.Depth < MaxRewriteDepth)
        {
            await RewriteLinksAsync(db, items, tenant, workspaceId, itemId, old.Title, old.NormalizedTitle, title, change.Depth, ct);
        }
    }

    /// <summary>Renamed: the notes linking to the old title of this note link to the new one.</summary>
    private static async Task RewriteLinksAsync(
        NotesDbContext database, IListItemStore items, Guid tenantId, Guid workspaceId, Guid itemId, string oldTitle, string oldNormalized, string newTitle, int depth, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var target = itemId;
        var old = oldNormalized;
        var ct = cancellationToken;
        var sources = (await db.Links.AsNoTracking().Where(l => l.TenantId == tenant && l.TargetItemId == target && l.NormalizedTarget == old).ToListAsync(ct))
            .Select(l => (l.SourceItemId, l.SourceListId))
            .Distinct()
            .ToList();

        // Changes made in reaction to an event carry its depth + 1 (loop protection).
        var store = items.AsSystem(new ChangeActor(tenant, null, depth + 1));
        foreach (var (sourceItemId, sourceListId) in sources)
        {
            var note = await store.GetAsync(workspaceId, sourceListId, sourceItemId, ct);
            var body = NoteTemplates.Text(note?.Fields[NoteTemplates.BodyField]);
            if (note is null || body is null)
            {
                continue;
            }

            var renamed = NoteMarkdown.RenameLinks(body, oldTitle, newTitle);
            if (renamed != body)
            {
                // The source is indexed again by its own update event.
                await store.UpdateAsync(workspaceId, sourceListId, sourceItemId, new JsonObject { [NoteTemplates.BodyField] = renamed }, null, ct);
            }
        }
    }

    private static async Task RemoveAsync(NotesDbContext database, Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        db.Links.RemoveRange(await db.Links.Where(l => l.TenantId == tenant && l.SourceItemId == id).ToListAsync(ct));
        foreach (var link in await db.Links.Where(l => l.TenantId == tenant && l.TargetItemId == id).ToListAsync(ct))
        {
            link.TargetItemId = null;
        }

        db.Notes.RemoveRange(await db.Notes.Where(n => n.TenantId == tenant && n.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string text) => text.Length > 1024 ? text[..1024] : text;
}
