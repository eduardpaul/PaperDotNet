using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>Builds search documents for list items: text of the fields, term labels, and the item's permission scope.</summary>
public sealed class ItemSearchDocuments : ISearchSource
{
    public const string ItemSourceType = "listItem";
    private const int BatchSize = 200;

    private static readonly HashSet<string> TextTypes = ["text", "note", "email", "url", "choice", "number", "currency", "date", "dateTime"];
    private static readonly HashSet<string> TermTypes = [ManagedMetadataFieldType.TypeName, KeywordsFieldType.TypeName];

    private readonly ListsDbContext _db;
    private readonly ITermStore _terms;
    private readonly ISearchIndex _index;
    private readonly IEnumerable<IItemSearchContributor> _contributors;

    /// <summary>Registered with a factory (the subscribers' code is generated ahead of time).</summary>
    internal ItemSearchDocuments(ListsDbContext db, ITermStore terms, ISearchIndex index, IEnumerable<IItemSearchContributor> contributors)
    {
        _db = db;
        _terms = terms;
        _index = index;
        _contributors = contributors;
    }

    public string SourceType => ItemSourceType;

    public async Task ReindexAsync(Guid tenantId, ISearchIndex index, Func<double, Task> progress, CancellationToken cancellationToken)
    {
        var db = _db;
        var tenant = tenantId;
        var ct = cancellationToken;
        var lists = await db.Lists.AsNoTracking().Where(l => l.TenantId == tenant && l.DeletedAt == null).OrderBy(l => l.Id).Select(l => l.Id).ToListAsync(ct);
        for (var i = 0; i < lists.Count; i++)
        {
            await IndexListAsync(tenantId, lists[i], index, cancellationToken);
            await progress((i + 1) / (double)lists.Count);
        }
    }

    /// <summary>Indexes one item again, or removes it when it is deleted, a folder, or its list is gone.</summary>
    public async Task IndexItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var db = _db;
        var tenant = tenantId;
        var id = itemId;
        var ct = cancellationToken;
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.TenantId == tenant && i.Id == id && i.DeletedAt == null, ct);
        var listId = item?.ListId ?? Guid.Empty;
        var list = item is null ? null : await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenant && l.Id == listId && l.DeletedAt == null, ct);
        if (item is null || item.IsFolder || list is null)
        {
            await _index.DeleteAsync(tenantId, [itemId], ct);
            return;
        }

        await _index.UpsertAsync(tenantId, await BuildAsync(tenantId, list, [item], ct), ct);
    }

    /// <summary>
    /// Gives the documents of the list's items in <paramref name="scopeId"/> that scope (after a permission change moved
    /// them, ADR-0035); text and passages stay.
    /// </summary>
    public async Task RefreshScopesAsync(Guid tenantId, Guid listId, Guid scopeId, CancellationToken cancellationToken)
    {
        var db = _db;
        var tenant = tenantId;
        var list = listId;
        var scope = scopeId;
        var take = BatchSize;
        var ct = cancellationToken;
        var after = Guid.Empty;
        while (true)
        {
            var from = after;
            var ids = await db.Items.AsNoTracking()
                .Where(i => i.TenantId == tenant && i.ListId == list && i.ScopeId == scope && !i.IsFolder && i.Id.CompareTo(from) > 0)
                .OrderBy(i => i.Id).Select(i => i.Id).Take(take).ToListAsync(ct);
            if (ids.Count > 0)
            {
                await _index.SetScopesAsync(tenantId, ids.ToDictionary(i => i, _ => scopeId), ct);
            }

            if (ids.Count < take)
            {
                return;
            }

            after = ids[^1];
        }
    }

    /// <summary>Replaces the documents of a list (none when the list is deleted).</summary>
    public Task IndexListAsync(Guid tenantId, Guid listId, CancellationToken cancellationToken) => IndexListAsync(tenantId, listId, _index, cancellationToken);

    private async Task IndexListAsync(Guid tenantId, Guid listId, ISearchIndex target, CancellationToken cancellationToken)
    {
        var db = _db;
        var tenant = tenantId;
        var id = listId;
        var take = BatchSize;
        var ct = cancellationToken;
        await target.DeleteContainerAsync(tenantId, listId, ct);
        var list = await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenant && l.Id == id && l.DeletedAt == null, ct);
        if (list is null)
        {
            return;
        }

        var after = Guid.Empty;
        while (true)
        {
            var from = after;
            var batch = await db.Items.AsNoTracking()
                .Where(i => i.TenantId == tenant && i.ListId == id && !i.IsFolder && i.DeletedAt == null && i.Id.CompareTo(from) > 0)
                .OrderBy(i => i.Id).Take(take).ToListAsync(ct);
            if (batch.Count > 0)
            {
                await target.UpsertAsync(tenantId, await BuildAsync(tenantId, list, batch, ct), ct);
            }

            if (batch.Count < take)
            {
                return;
            }

            after = batch[^1].Id;
        }
    }

    private async Task<List<SearchDocumentData>> BuildAsync(Guid tenantId, ListDefinition list, IReadOnlyList<ListItem> items, CancellationToken ct)
    {
        var fields = new Dictionary<Guid, List<FieldDefinition>>();
        foreach (var contentTypeId in items.Select(i => i.ContentTypeId).Distinct())
        {
            var db = _db;
            var tenant = tenantId;
            var id = contentTypeId;
            var token = ct;
            var json = await db.ContentTypes.AsNoTracking().Where(c => c.TenantId == tenant && c.Id == id).Select(c => c.Fields).FirstOrDefaultAsync(token);
            fields[contentTypeId] = json is null ? [] : ListsJsonText.Fields(json);
        }

        var values = items.ToDictionary(i => i.Id, i => JsonNode.Parse(i.Fields)?.AsObject() ?? []);
        var termIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            foreach (var field in fields[item.ContentTypeId].Where(f => TermTypes.Contains(f.Type)))
            {
                termIds.UnionWith(Ids(values[item.Id][field.Name]));
            }
        }

        var labels = termIds.Count == 0 ? new Dictionary<Guid, IReadOnlyList<string>>() : await _terms.GetLabelsAsync(tenantId, termIds, ct);
        var itemIds = items.Select(i => i.Id).ToList();
        var extra = new List<IReadOnlyDictionary<Guid, ItemSearchContent>>();
        foreach (var contributor in _contributors)
        {
            extra.Add(await contributor.GetContentAsync(tenantId, itemIds, ct));
        }

        return [.. items.Select(item =>
        {
            var body = new StringBuilder();
            var keywords = new StringBuilder();
            var itemTerms = new List<Guid>();
            foreach (var field in fields[item.ContentTypeId])
            {
                var value = values[item.Id][field.Name];
                var weight = field.Search ?? FieldSearchWeight.Normal;
                if (value is null)
                {
                    continue;
                }

                if (TermTypes.Contains(field.Type))
                {
                    // Tags always count for facets and tag filters, even when not full-text indexed.
                    itemTerms.AddRange(Ids(value));
                }

                var target = weight == FieldSearchWeight.High ? keywords : body;
                if (weight == FieldSearchWeight.None)
                {
                    continue;
                }

                if (TermTypes.Contains(field.Type))
                {
                    foreach (var id in Ids(value))
                    {
                        target.AppendJoin(' ', labels.GetValueOrDefault(id) ?? []).Append('\n');
                    }
                }
                else if (TextTypes.Contains(field.Type))
                {
                    var texts = value is JsonArray array ? array.Select(Text) : [Text(value)];
                    target.AppendJoin(' ', texts).Append('\n');
                }
            }

            string? language = null;
            List<string> pages = [];
            foreach (var content in extra.Select(e => e.GetValueOrDefault(item.Id)).OfType<ItemSearchContent>())
            {
                if (content.Pages is { } contentPages && pages.Count == 0)
                {
                    pages = [.. contentPages];
                }
                else
                {
                    body.Append(content.Pages is { } other ? string.Join('\n', other) : content.Text).Append('\n');
                }

                language ??= content.Language;
            }

            return new SearchDocumentData(
                item.Id, ItemSourceType, list.WorkspaceId, list.Id, item.ContentTypeId, item.Title, body.ToString(),
                item.ScopeId, itemTerms, item.CreatedBy, item.UpdatedAt)
            {
                Keywords = keywords.ToString(),
                Language = language,
                Pages = pages,
            };
        })];
    }

    private static IEnumerable<Guid> Ids(JsonNode? value) => value switch
    {
        JsonArray array => array.Select(v => v is JsonValue s && s.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty),
        JsonValue single when single.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) => [id],
        _ => [],
    };

    private static string Text(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => value?.ToJsonString() ?? string.Empty,
    };
}

/// <summary>Keeps the search index in sync with item and list changes (asynchronous, idempotent; generated ahead of time).</summary>
public static class ItemSearchSubscriber
{
    public static Task Handle(ItemAdded e, ItemSearchDocuments documents, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(e.TenantId, e.ItemId, cancellationToken);

    public static Task Handle(ItemUpdated e, ItemSearchDocuments documents, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(e.TenantId, e.ItemId, cancellationToken);

    public static Task Handle(ItemRestored e, ItemSearchDocuments documents, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(e.TenantId, e.ItemId, cancellationToken);

    public static Task Handle(ItemDeleted e, ItemSearchDocuments documents, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(e.TenantId, e.ItemId, cancellationToken);

    public static Task Handle(ItemPurged e, ItemSearchDocuments documents, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(e.TenantId, e.ItemId, cancellationToken);

    public static Task Handle(ListDeleted e, ItemSearchDocuments documents, CancellationToken cancellationToken) =>
        documents.IndexListAsync(e.TenantId, e.ListId, cancellationToken);
}
