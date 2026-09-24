using System.Collections.Frozen;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

internal sealed class ScopeCatalog : IScopeCatalog
{
    private readonly FrozenDictionary<string, ScopeDefinition> _scopes;

    public ScopeCatalog(IEnumerable<ScopeDefinition> scopes)
    {
        _scopes = scopes
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ScopeDefinition> All => _scopes.Values;

    public bool Contains(string scope) => _scopes.ContainsKey(scope);
}
