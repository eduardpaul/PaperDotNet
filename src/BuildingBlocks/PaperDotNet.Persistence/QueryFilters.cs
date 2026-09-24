namespace PaperDotNet.Persistence;

/// <summary>Names of the global query filters (EF Core 10 named filters).</summary>
public static class QueryFilters
{
    /// <summary>Tenant isolation. Never disabled in module code (enforced by architecture tests).</summary>
    public const string Tenant = "Tenant";

    /// <summary>Hides soft-deleted rows. May be disabled for recycle-bin queries.</summary>
    public const string SoftDelete = "SoftDelete";
}
