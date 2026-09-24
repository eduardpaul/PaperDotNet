namespace PaperDotNet.Lists.Contracts;

/// <summary>Extra searchable content of an item, e.g. the text of its file.</summary>
/// <param name="Text">Text added to the item's search body.</param>
/// <param name="Language">Language of the text for stemming (e.g. <c>english</c>), or null.</param>
public sealed record ItemSearchContent(string Text, string? Language);

/// <summary>
/// Adds content to the search documents of list items (SRC-01): Documents contributes the
/// text of files. Called in batches when items are indexed; call
/// <see cref="IListItemStore.ReindexAsync"/> when the content changed.
/// </summary>
public interface IItemSearchContributor
{
    Task<IReadOnlyDictionary<Guid, ItemSearchContent>> GetContentAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
}
