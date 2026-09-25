using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>
/// The search index of a list must be rebuilt: the list was deleted or its
/// permissions (or the scopes of many items) changed.
/// </summary>
public sealed record ListIndexInvalidated : IntegrationEvent
{
    public required Guid ListId { get; init; }

    internal static ListIndexInvalidated For(ITenantContext tenant, ICurrentUser user, Guid listId) => new()
    {
        TenantId = tenant.TenantId!.Value,
        TenantIdentifier = tenant.TenantIdentifier!,
        UserId = user.UserId,
        ListId = listId,
    };
}

/// <summary>Builds search documents for list items: text of the fields, term labels, and who may read them.</summary>
internal sealed class ListItemSearchDocuments(ListsDbContext db, ITermStore terms, ISearchIndex index, IEnumerable<IItemSearchContributor> contributors) : ISearchSource
{
    public const string ItemSourceType = "listItem";
    private const int BatchSize = 200;

    private static readonly HashSet<string> TextTypes = ["text", "note", "email", "url", "choice", "number", "currency", "date", "dateTime"];
    private static readonly HashSet<string> TermTypes = [ManagedMetadataFieldType.TypeName, KeywordsFieldType.TypeName];

    public string SourceType => ItemSourceType;

    public async Task ReindexAsync(ISearchIndex target, Func<double, Task> progress, CancellationToken cancellationToken)
    {
        var lists = await db.Lists.AsNoTracking().OrderBy(l => l.Id).Select(l => l.Id).ToListAsync(cancellationToken);
        for (var i = 0; i < lists.Count; i++)
        {
            await IndexListAsync(lists[i], target, cancellationToken);
            await progress((i + 1) / (double)lists.Count);
        }
    }

    /// <summary>Indexes one item again, or removes it when it is deleted or a folder.</summary>
    public async Task IndexItemAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        var list = item is null ? null : await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == item.ListId, ct);
        if (item is null || item.IsFolder || list is null)
        {
            await index.DeleteAsync([itemId], ct);
            return;
        }

        await index.UpsertAsync(await BuildAsync(list, [item], ct), ct);
    }

    /// <summary>Replaces the documents of a list (none when the list is deleted).</summary>
    public Task IndexListAsync(Guid listId, CancellationToken ct) => IndexListAsync(listId, index, ct);

    private async Task IndexListAsync(Guid listId, ISearchIndex target, CancellationToken ct)
    {
        await target.DeleteContainerAsync(listId, ct);
        var list = await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null)
        {
            return;
        }

        Guid? after = null;
        while (true)
        {
            var batch = await db.Items.AsNoTracking()
                .Where(i => i.ListId == listId && !i.IsFolder && (after == null || i.Id.CompareTo(after.Value) > 0))
                .OrderBy(i => i.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                return;
            }

            await target.UpsertAsync(await BuildAsync(list, batch, ct), ct);
            after = batch[^1].Id;
        }
    }

    private async Task<List<SearchDocumentData>> BuildAsync(ListDefinition list, IReadOnlyList<ListItem> items, CancellationToken ct)
    {
        var contentTypeIds = items.Select(i => i.ContentTypeId).Distinct().ToList();
        var fields = (await db.ContentTypes.AsNoTracking().Where(c => contentTypeIds.Contains(c.Id)).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.Fields);
        var scopes = items.Select(i => i.ScopeId ?? list.Id).Distinct().ToList();
        var grants = (list.HasUniquePermissions || items.Any(i => i.ScopeId is not null))
            ? (await db.Grants.AsNoTracking().Where(g => scopes.Contains(g.ObjectId)).ToListAsync(ct)).ToLookup(g => g.ObjectId)
            : Enumerable.Empty<PermissionGrant>().ToLookup(g => g.ObjectId);

        var values = items.ToDictionary(i => i.Id, i => JsonNode.Parse(i.Fields)!.AsObject());
        var termIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            foreach (var field in fields.GetValueOrDefault(item.ContentTypeId, []).Where(f => TermTypes.Contains(f.Type)))
            {
                termIds.UnionWith(Ids(values[item.Id][field.Name]));
            }
        }

        var labels = await terms.GetLabelsAsync(termIds, ct);
        var itemIds = items.Select(i => i.Id).ToList();
        var extra = new List<IReadOnlyDictionary<Guid, ItemSearchContent>>();
        foreach (var contributor in contributors)
        {
            extra.Add(await contributor.GetContentAsync(itemIds, ct));
        }

        return items.Select(item =>
        {
            var body = new StringBuilder();
            var keywords = new StringBuilder();
            var itemTerms = new List<Guid>();
            foreach (var field in fields.GetValueOrDefault(item.ContentTypeId, []))
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
                Principals(list, item, grants), itemTerms, item.CreatedBy, item.UpdatedAt)
            {
                Keywords = keywords.ToString(),
                Language = language,
                Pages = pages,
            };
        }).ToList();
    }

    /// <summary>Who may read the item (same rules as <see cref="ListAccess"/>, ADR-0011).</summary>
    private static List<string> Principals(ListDefinition list, ListItem item, ILookup<Guid, PermissionGrant> grants)
    {
        var principals = new List<string> { SearchPrincipals.WorkspaceOwner(list.WorkspaceId) };
        if (item.ScopeId is null && !list.HasUniquePermissions)
        {
            principals.Add(SearchPrincipals.WorkspaceMember(list.WorkspaceId));
            return principals;
        }

        principals.AddRange(grants[item.ScopeId ?? list.Id].Select(g =>
            g.PrincipalType == PrincipalType.User ? SearchPrincipals.User(g.PrincipalId) : SearchPrincipals.Group(g.PrincipalId)));
        return principals;
    }

    private static IEnumerable<Guid> Ids(JsonNode? value) => value switch
    {
        JsonArray array => array.Select(v => Guid.TryParse(v?.GetValue<string>(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty),
        JsonValue single when Guid.TryParse(single.GetValue<string>(), out var id) => [id],
        _ => [],
    };

    private static string Text(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => value?.ToJsonString() ?? string.Empty,
    };
}

/// <summary>Keeps the search index in sync with item and list changes (asynchronous, idempotent).</summary>
internal sealed class ItemSearchIndexer(ListItemSearchDocuments documents)
    : IEventSubscriber<ItemAdded>, IEventSubscriber<ItemUpdated>, IEventSubscriber<ItemRestored>, IEventSubscriber<ItemDeleted>,
      IEventSubscriber<ListIndexInvalidated>
{
    public Task HandleAsync(ItemAdded integrationEvent, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(integrationEvent.ItemId, cancellationToken);

    public Task HandleAsync(ItemUpdated integrationEvent, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(integrationEvent.ItemId, cancellationToken);

    public Task HandleAsync(ItemRestored integrationEvent, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(integrationEvent.ItemId, cancellationToken);

    public Task HandleAsync(ItemDeleted integrationEvent, CancellationToken cancellationToken) =>
        documents.IndexItemAsync(integrationEvent.ItemId, cancellationToken);

    public Task HandleAsync(ListIndexInvalidated integrationEvent, CancellationToken cancellationToken) =>
        documents.IndexListAsync(integrationEvent.ListId, cancellationToken);
}
