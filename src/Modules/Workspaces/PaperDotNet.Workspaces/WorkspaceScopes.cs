using PaperDotNet.Abstractions;

namespace PaperDotNet.Workspaces;

public static class WorkspaceScopes
{
    public const string Read = "workspace.read";
    public const string Create = "workspace.create";

    /// <summary>Administer every workspace in the organization, member or not.</summary>
    public const string Manage = "workspace.manage";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See the workspaces you are a member of.", GrantedToMembers: true),
        new(Create, "Create workspaces.", GrantedToMembers: true),
        new(Manage, "Administer all workspaces of the organization."),
    ];
}
