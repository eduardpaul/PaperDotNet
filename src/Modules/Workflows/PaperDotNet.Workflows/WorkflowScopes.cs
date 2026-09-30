using PaperDotNet.Abstractions;

namespace PaperDotNet.Workflows;

public static class WorkflowScopes
{
    public const string Read = "workflow.read";
    public const string Write = "workflow.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See workflows and their runs.", GrantedToMembers: true),
        new(Write, "Start workflows on items and (as workspace manager) change workflows.", GrantedToMembers: true),
    ];
}
