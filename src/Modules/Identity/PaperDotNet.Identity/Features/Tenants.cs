using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Identity.Features;

public sealed class BootstrapOptions
{
    public string TenantIdentifier { get; set; } = "default";

    public string TenantName { get; set; } = "Default";

    /// <summary>First administrator, created with the tenant on first start when <see cref="AdminPassword"/> is set.</summary>
    public string AdminUserName { get; set; } = "admin";

    public string AdminPassword { get; set; } = "";
}

/// <summary>Creates tenants with their first administrator.</summary>
public sealed class TenantProvisioner(IdentityDbContext db, IPasswordHasher<User> hasher, TimeProvider time)
{
    public async Task<Tenant?> FindAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var context = db;
        var id = identifier;
        var ct = cancellationToken;
        return await context.Tenants.Where(t => t.Identifier == id).FirstOrDefaultAsync(ct);
    }

    public async Task<(Tenant Tenant, User Admin)> CreateAsync(string identifier, string name, string adminUserName, string adminPassword, CancellationToken cancellationToken = default)
    {
        if (adminPassword.Length < Users.MinPasswordLength)
        {
            throw new ArgumentException($"The administrator password needs at least {Users.MinPasswordLength} characters.", nameof(adminPassword));
        }

        var tenant = new Tenant { Id = Ids.New(), Identifier = identifier, Name = name, CreatedAt = time.GetUtcNow() };
        var admin = Users.New(tenant.Id, adminUserName, null, isAdmin: true);
        admin.PasswordHash = hasher.HashPassword(admin, adminPassword);
        db.Tenants.Add(tenant);
        db.Users.Add(admin);
        await db.SaveChangesAsync(cancellationToken);
        return (tenant, admin);
    }
}

/// <summary>
/// The tenant directory (Tenancy.Contracts) over the Identity tables, until the Tenancy module is ported. Tenants have
/// no host names yet: they are chosen by identifier when signing in.
/// </summary>
internal sealed class TenantDirectory(IdentityDbContext db, TimeProvider time) : ITenantDirectory
{
    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var context = db;
        var ct = cancellationToken;
        var tenants = await context.Tenants.AsNoTracking().OrderBy(t => t.Identifier).ToListAsync(ct);
        return [.. tenants.Select(Summary)];
    }

    public async Task<TenantSummary?> FindAsync(string identifier, CancellationToken cancellationToken)
    {
        var tenant = await FindTenantAsync(identifier, cancellationToken);
        return tenant is null ? null : Summary(tenant);
    }

    public async Task<TenantSummary> CreateAsync(string identifier, string name, IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        var tenant = new Tenant { Id = Ids.New(), Identifier = identifier, Name = name, CreatedAt = time.GetUtcNow() };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        return Summary(tenant);
    }

    public async Task SetStatusAsync(string identifier, TenantStatus status, CancellationToken cancellationToken)
    {
        var tenant = await FindTenantAsync(identifier, cancellationToken)
            ?? throw new InvalidOperationException($"The tenant '{identifier}' does not exist.");
        tenant.Status = status == TenantStatus.Suspended ? TenantStatuses.Suspended : TenantStatuses.Active;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Whether users of the tenant may get tokens.</summary>
    public static Task<bool> IsActiveAsync(IdentityDbContext database, Guid tenantId, CancellationToken cancellationToken)
    {
        var context = database;
        var id = tenantId;
        var active = TenantStatuses.Active;
        var ct = cancellationToken;
        return context.Tenants.AnyAsync(t => t.Id == id && t.Status == active, ct);
    }

    private Task<Tenant?> FindTenantAsync(string identifier, CancellationToken cancellationToken)
    {
        var context = db;
        var id = identifier;
        var ct = cancellationToken;
        return context.Tenants.Where(t => t.Identifier == id).FirstOrDefaultAsync(ct);
    }

    private static TenantSummary Summary(Tenant tenant) => new(
        tenant.Id,
        tenant.Identifier,
        tenant.Name,
        tenant.Status == TenantStatuses.Suspended ? TenantStatus.Suspended : TenantStatus.Active,
        [],
        tenant.CreatedAt);
}

public static partial class Bootstrap
{
    /// <summary>Creates the configured tenant and administrator on first start.</summary>
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<BootstrapOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.AdminUserName) || string.IsNullOrEmpty(options.AdminPassword))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var provisioner = scope.ServiceProvider.GetRequiredService<TenantProvisioner>();
        if (await provisioner.FindAsync(options.TenantIdentifier, cancellationToken) is not null)
        {
            return;
        }

        await provisioner.CreateAsync(options.TenantIdentifier, options.TenantName, options.AdminUserName, options.AdminPassword, cancellationToken);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Bootstrap));
        LogCreated(logger, options.TenantIdentifier, options.AdminUserName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created tenant {Tenant} with the administrator {UserName}.")]
    private static partial void LogCreated(ILogger logger, string tenant, string userName);
}
