using PaperDotNet.Search.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Store tests prepare chunks as pipeline activities do; the backend never chooses chunking.</summary>
internal static class SearchStoreTestSeed
{
    public static Task SeedAsync(this ISearchStore store, IReadOnlyCollection<SearchDocumentData> documents, CancellationToken ct) =>
        store.UpsertAsync(documents.Select(d => d with { Chunks = Passages.Split(d) }).ToList(), ct);
}
