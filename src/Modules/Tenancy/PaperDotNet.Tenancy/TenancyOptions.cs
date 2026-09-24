using System.ComponentModel.DataAnnotations;

namespace PaperDotNet.Tenancy;

/// <summary>Configuration section <c>Tenancy</c>.</summary>
public sealed class TenancyOptions
{
    public const string Section = "Tenancy";

    /// <summary>
    /// Tenant used when nothing else resolves one. Set for single-tenant
    /// self-hosting (the default is <c>default</c>); clear it when hosting
    /// several tenants.
    /// </summary>
    public string? DefaultTenant { get; set; } = "default";

    /// <summary>Finbuckle host template, e.g. <c>__tenant__.dms.example.com</c>. Off when empty.</summary>
    public string? HostTemplate { get; set; }

    /// <summary>Allow selecting the tenant with a request header (development and tests).</summary>
    public bool AllowHeader { get; set; }

    [Required]
    public string HeaderName { get; set; } = "X-Tenant";
}
