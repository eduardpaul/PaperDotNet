namespace PaperDotNet.Host.Bootstrap;

/// <summary>Configuration section <c>Bootstrap</c>: what the first start creates.</summary>
public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";

    /// <summary>Tenant created when no tenant exists. Empty disables it.</summary>
    public string? TenantIdentifier { get; set; } = "default";

    public string TenantName { get; set; } = "Default";

    public string AdminUserName { get; set; } = "admin";

    /// <summary>Admin password. The admin is only created when this is set and the tenant has no users.</summary>
    public string? AdminPassword { get; set; }

    public string? AdminEmail { get; set; }
}

/// <summary>Configuration section <c>Database</c>.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Apply migrations at startup. Convenient for single-node self-hosting; use `paperdotnet migrate` otherwise.</summary>
    public bool MigrateOnStartup { get; set; } = true;
}
