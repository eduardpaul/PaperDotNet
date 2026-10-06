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
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Lists.Features;

/// <summary>The search index of a list must be rebuilt (e.g. the list was deleted). Permission changes do not need it.</summary>
public sealed record ListIndexInvalidated : IntegrationEvent
{
    public required Guid ListId { get; init; }

    public Guid WorkspaceId { get; init; }

    internal static ListIndexInvalidated For(ITenantContext tenant, ICurrentUser user, Guid listId, Guid workspaceId) => new()
    {
        TenantId = tenant.TenantId!.Value,
        TenantIdentifier = tenant.TenantIdentifier!,
        UserId = user.UserId,
        ListId = listId,
        WorkspaceId = workspaceId,
    };
}

/// <summary>Builds search documents for list items: text of the fields, term labels, and the item's permission scope.</summary>
internal sealed class ListItemSearchDocuments(ListsDbContext db, ITermStore terms, IEnumerable<IItemSearchContributor> contributors, IWorkflowTriggers triggers) : ISearchItemSource
{
    public const string ItemSourceType = "listItem";

    private static readonly HashSet<string> TextTypes = ["text", "note", "email", "url", "choice", "number", "currency", "date", "dateTime"];
    private static readonly HashSet<string> TermTypes = [ManagedMetadataFieldType.TypeName, KeywordsFieldType.TypeName];

    public string SourceType => ItemSourceType;

    public async Task<SearchDocumentData?> GetDocumentAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        var list = item is null ? null : await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == item.ListId, ct);
        return item is null || item.IsFolder || list is null ? null : (await BuildAsync(list, [item], ct))[0];
    }

    /// <summary>The item's last change and its list's schema version: what its document is built from, apart from comments and term labels.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetStampsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.ToArray();
        return (await db.Items.AsNoTracking().Where(i => EF.Parameter(ids).Contains(i.Id) && !i.IsFolder)
                .Select(i => new { i.Id, i.ListId, i.ContentTypeId, i.UpdatedAt }).ToListAsync(ct))
            .ToDictionary(i => i.Id, i => $"{i.ListId:N}:{i.ContentTypeId:N}:{i.UpdatedAt.UtcTicks}");
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> GetScopesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        var ids = itemIds.ToArray();
        return await db.Items.AsNoTracking().Where(i => EF.Parameter(ids).Contains(i.Id) && !i.IsFolder).ToDictionaryAsync(i => i.Id, i => i.ScopeId, ct);
    }

    /// <summary>Requests indexing through the workflow transport; never writes derived search data.</summary>
    public async Task IndexItemAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        var list = item is null ? null : await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == item.ListId, ct);
        if (item is not null && list is not null && !item.IsFolder)
        {
            await triggers.RaiseAsync(SearchTriggers.Requested, list.WorkspaceId, new WorkflowItem(list.WorkspaceId, list.Id, itemId), null, ct);
        }
    }

    /// <summary>The workspace of a list, or null when it is gone.</summary>
    public async Task<Guid?> WorkspaceOfAsync(Guid listId, CancellationToken ct) =>
        await db.Lists.AsNoTracking().Where(l => l.Id == listId).Select(l => (Guid?)l.WorkspaceId).FirstOrDefaultAsync(ct);

    private async Task<List<SearchDocumentData>> BuildAsync(ListDefinition list, IReadOnlyList<ListItem> items, CancellationToken ct)
    {
        var contentTypeIds = items.Select(i => i.ContentTypeId).Distinct().ToList();
        var fields = (await db.ContentTypes.AsNoTracking().Where(c => contentTypeIds.Contains(c.Id)).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.Fields);

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
            var searchFields = new List<SearchField>();
            foreach (var field in fields.GetValueOrDefault(item.ContentTypeId, []))
            {
                var value = values[item.Id][field.Name];
                var weight = field.Search ?? FieldSearchWeight.Normal;
                if (value is null)
                {
                    continue;
                }

                if (SearchFieldOf(field, value) is { Values.Count: > 0 } searchField)
                {
                    searchFields.Add(searchField);
                }

                if (TermTypes.Contains(field.Type))
                {
                    // Tags always count for facets and tag filters, even when not full-text indexed.
                    itemTerms.AddRange(Ids(value));
                }

                if (field.Name == "title") { continue; }
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
                Fields = searchFields,
            };
        }).ToList();
    }

    /// <summary>
    /// The field as a typed search field (ADR-0043), filterable with <c>$filter</c> on <c>/v1.0/search</c>; null for
    /// fields that are only searched as text (notes) or have no value of the expected shape.
    /// </summary>
    internal static SearchField? SearchFieldOf(FieldDefinition field, JsonNode value)
    {
        var nodes = value is JsonArray array ? array.Where(v => v is not null).Select(v => v!) : [value];
        (SearchFieldKind Kind, Func<JsonNode, SearchValue?> Convert)? mapping = field.Type switch
        {
            "text" or "email" or "url" or "choice" => (SearchFieldKind.Keyword, v => v is JsonValue j && j.TryGetValue<string>(out var s) ? SearchValue.Of(s) : null),
            "number" or "currency" => (SearchFieldKind.Number, v => v is JsonValue j && j.TryGetValue<double>(out var d) ? SearchValue.Of(d) : null),
            "boolean" => (SearchFieldKind.Boolean, v => v is JsonValue j && j.TryGetValue<bool>(out var b) ? SearchValue.Of(b) : null),
            "date" => (SearchFieldKind.Date, v => v is JsonValue j && j.TryGetValue<string>(out var s)
                && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? SearchValue.Of(d) : null),
            "dateTime" => (SearchFieldKind.DateTime, v => v is JsonValue j && j.TryGetValue<string>(out var s)
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? SearchValue.Of(t) : null),
            "person" or "lookup" => (SearchFieldKind.Reference, v => Ids(v).Select(SearchValue.Of).Cast<SearchValue?>().FirstOrDefault()),
            ManagedMetadataFieldType.TypeName or KeywordsFieldType.TypeName => (SearchFieldKind.Terms, v => Ids(v).Select(SearchValue.Of).Cast<SearchValue?>().FirstOrDefault()),
            _ => null,
        };
        if (mapping is not { } map)
        {
            return null;
        }

        return new SearchField(field.Name, map.Kind, nodes.Select(map.Convert).OfType<SearchValue>().ToList()) { Indexed = field.Indexed };
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

/// <summary>
/// Tells search about list changes that items' own events do not cover (asynchronous, idempotent): a list deleted or its
/// schema changed, and items moved to other permission scopes (only their scopes change in the index).
/// </summary>
internal sealed class ItemSearchIndexer(ListItemSearchDocuments documents, IWorkflowTriggers triggers)
    : IEventSubscriber<ListIndexInvalidated>, IEventSubscriber<ItemScopesChanged>
{
    public async Task HandleAsync(ListIndexInvalidated integrationEvent, CancellationToken cancellationToken)
    {
        // Events saved before the workspace was part of them: the list is deleted, so its records are hidden by queries.
        if (integrationEvent.WorkspaceId != Guid.Empty)
        {
            await triggers.RaiseAsync(SearchTriggers.ContainerChanged, integrationEvent.WorkspaceId, null,
                new JsonObject { ["containerId"] = integrationEvent.ListId.ToString() }, integrationEvent, cancellationToken);
        }
    }

    public async Task HandleAsync(ItemScopesChanged integrationEvent, CancellationToken cancellationToken)
    {
        if (await documents.WorkspaceOfAsync(integrationEvent.ListId, cancellationToken) is { } workspaceId)
        {
            await triggers.RaiseAsync(SearchTriggers.ScopesChanged, workspaceId, null,
                new JsonObject { ["itemIds"] = new JsonArray([.. integrationEvent.ItemIds.Select(id => JsonValue.Create(id.ToString()))]) },
                integrationEvent, cancellationToken);
        }
    }
}
