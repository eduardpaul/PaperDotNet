using PaperDotNet.Abstractions;

namespace PaperDotNet.Lists;

public static class ListScopes
{
    public const string Read = "list.read";
    public const string Write = "list.write";
    public const string ContentTypeRead = "contentType.read";
    public const string ContentTypeManage = "contentType.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "Read lists, libraries and their items in your workspaces.", GrantedToMembers: true),
        new(Write, "Create and edit items in your workspaces.", GrantedToMembers: true),
        new(ContentTypeRead, "See the organization's content types.", GrantedToMembers: true),
        new(ContentTypeManage, "Create and change content types."),
    ];
}
