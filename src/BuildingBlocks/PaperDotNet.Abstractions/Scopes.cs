namespace PaperDotNet.Abstractions;

/// <summary>Permission scopes in access tokens (ADR-0011). Administrators get all; members the list scopes.</summary>
public static class Scopes
{
    public const string ListsRead = "lists.read";
    public const string ListsWrite = "lists.write";
    public const string UsersManage = "users.manage";
    public const string AuditRead = "audit.read";
    public const string WorkflowsManage = "workflows.manage";

    /// <summary>The scopes of a user, space-separated as in a token.</summary>
    public static string For(bool isAdmin) => isAdmin
        ? $"{ListsRead} {ListsWrite} {UsersManage} {AuditRead} {WorkflowsManage}"
        : $"{ListsRead} {ListsWrite}";
}
