using PaperDotNet.Abstractions;

namespace PaperDotNet.Taxonomy;

public static class TaxonomyScopes
{
    public const string Read = "taxonomy.read";
    public const string Manage = "taxonomy.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Read the term store and add keywords or terms to open term sets.", GrantedToMembers: true),
        new(Manage, "Manage term groups, term sets and terms."),
    ];
}
