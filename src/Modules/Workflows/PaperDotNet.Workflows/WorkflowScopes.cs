using PaperDotNet.Abstractions;

namespace PaperDotNet.Workflows;

public static class WorkflowScopes
{
    public const string Read = "workflow.read";
    public const string Write = "workflow.write";

    public static readonly ScopeDefinition[] All =
    [
        new(Read, "See workflows and their runs.", GrantedToMembers: true),

        // Members get it back with workspaces (T05), where the workspace manager decides; until then workflows belong
        // to the whole organization.
        new(Write, "Create, change and start workflows."),
    ];
}
