namespace PaperDotNet.Lists.Contracts;

/// <summary>Versioned item text, available independently of whether it is included in search.</summary>
public sealed record ItemTextContent(string Revision, string Text, string? Language)
{
    public IReadOnlyList<string> Pages { get; init; } = [];
    public bool Ready { get; init; } = true;
}

public interface IItemTextSource
{
    Task<IReadOnlyDictionary<Guid, ItemTextContent>> GetTextAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
}

/// <summary>Extra searchable content of an item, e.g. the text of its file.</summary>
/// <param name="Text">Text added to the item's search body (ignored when <see cref="Pages"/> is set).</param>
/// <param name="Language">Language of the text for stemming (e.g. <c>english</c>), or null.</param>
public sealed record ItemSearchContent(string Text, string? Language)
{
    /// <summary>The text page by page (page 1 first), e.g. of a PDF, so search can point to the matching page (SRC-09).</summary>
    public IReadOnlyList<string>? Pages { get; init; }
}

/// <summary>
/// Adds content to the search documents of list items (SRC-01): Documents contributes the
/// text of files. Called in batches when items are indexed; call
/// <see cref="IListItemStore.ReindexAsync"/> when the content changed.
/// </summary>
public interface IItemSearchContributor
{
    Task<IReadOnlyDictionary<Guid, ItemSearchContent>> GetContentAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
}

/// <summary>A page of an item as an image (a JPEG, for example).</summary>
public sealed record ItemPageImage(int Page, string MediaType, byte[] Content);

/// <summary>
/// The pages of items as images, for AI models that read images (vision, AI activities with <c>includeImages</c>):
/// Documents renders the pages of an item's current file.
/// </summary>
public interface IItemPageImageSource
{
    /// <summary>The first <paramref name="maxPages"/> pages of the item as images, page 1 first; empty when it has none.</summary>
    Task<IReadOnlyList<ItemPageImage>> GetPageImagesAsync(Guid itemId, int maxPages, CancellationToken cancellationToken);
}
