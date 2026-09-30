using PaperDotNet.Abstractions;

namespace PaperDotNet.Audit;

public static class AuditScopes
{
    public const string Read = "audit.read";

    public static readonly ScopeDefinition[] All = [new(Read, "Read the organization's audit log.")];
}
