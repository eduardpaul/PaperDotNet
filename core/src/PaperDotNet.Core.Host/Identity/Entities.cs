using System.ComponentModel.DataAnnotations;

namespace PaperDotNet.Core.Host.Identity;

// Entity classes are not sealed: EF Core's precompiled materializers test them for IInjectableService (ADR-0039).
#pragma warning disable CA1852

public class Tenant
{
    public Guid Id { get; set; }

    /// <summary>Stable name used to sign in (<c>tenant</c> in the token request).</summary>
    public string Identifier { get; set; } = "";

    public string Name { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

public class User : ITenantOwned, IVersioned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string UserName { get; set; } = "";

    /// <summary>Upper-case invariant user name, unique per tenant.</summary>
    public string NormalizedUserName { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public bool IsAdmin { get; set; }

    public bool IsDisabled { get; set; }

    /// <summary>Changes with the password or when access ends; refresh tokens carry it.</summary>
    public string SecurityStamp { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    [ConcurrencyCheck]
    public uint Version { get; set; }
}

#pragma warning restore CA1852
