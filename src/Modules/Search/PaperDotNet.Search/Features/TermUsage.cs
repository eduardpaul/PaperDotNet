using PaperDotNet.Search.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>How often terms are used as tags (TAX-05), counted by the search store.</summary>
internal sealed class TermUsage(ISearchStore store) : ITermUsage
{
    public Task<IReadOnlyDictionary<Guid, int>> CountAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken) =>
        store.CountTermsAsync(termIds, cancellationToken);
}
