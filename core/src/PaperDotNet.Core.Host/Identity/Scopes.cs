namespace PaperDotNet.Core.Host.Identity;

/// <summary>Permission scopes in access tokens. Administrators get all; members the list scopes.</summary>
public static class Scopes
{
    public const string ListsRead = "lists.read";
    public const string ListsWrite = "lists.write";
    public const string UsersManage = "users.manage";
    public const string AuditRead = "audit.read";
    public const string WorkflowsManage = "workflows.manage";

    public static string For(User user) => user.IsAdmin
        ? $"{ListsRead} {ListsWrite} {UsersManage} {AuditRead} {WorkflowsManage}"
        : $"{ListsRead} {ListsWrite}";
}
