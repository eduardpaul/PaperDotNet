using PaperDotNet.Abstractions;

namespace PaperDotNet.Lists;

public static class ListScopes
{
    public const string Read = "list.read";
    public const string Write = "list.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Read lists, libraries and their items.", GrantedToMembers: true),
        new(Write, "Create and edit lists and items.", GrantedToMembers: true),
    ];
}
