using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Core.Host.Data;

namespace PaperDotNet.Core.Host.Identity;

public sealed class BootstrapOptions
{
    public string TenantIdentifier { get; set; } = "default";

    public string TenantName { get; set; } = "Default";

    /// <summary>First administrator, created with the tenant on first start when set.</summary>
    public string AdminUserName { get; set; } = "";

    public string AdminPassword { get; set; } = "";
}

/// <summary>Creates tenants with their first administrator.</summary>
public sealed class TenantProvisioner(CoreDb db, IPasswordHasher<User> hasher, TimeProvider time)
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

internal static partial class Bootstrap
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
