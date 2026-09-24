using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Host.Bootstrap;

/// <summary>Creates the default tenant and its first administrator on a fresh installation.</summary>
internal sealed partial class TenantBootstrapper(
    ITenantDirectory tenants,
    ITenantScopeFactory tenantScopes,
    IOptions<BootstrapOptions> options,
    ILogger<TenantBootstrapper> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.TenantIdentifier))
        {
            return;
        }

        var tenant = await tenants.FindAsync(settings.TenantIdentifier, cancellationToken);
        if (tenant is null)
        {
            if ((await tenants.ListAsync(cancellationToken)).Count > 0)
            {
                return;
            }

            tenant = await tenants.CreateAsync(settings.TenantIdentifier, settings.TenantName, [], cancellationToken);
            LogTenantCreated(tenant.Identifier);
        }

        if (string.IsNullOrEmpty(settings.AdminPassword))
        {
            return;
        }

        await using var scope = tenantScopes.CreateScope(tenant.Id, tenant.Identifier);
        var users = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        if (!await users.AnyUsersAsync(cancellationToken))
        {
            await users.CreateUserAsync(
                new NewUser(settings.AdminUserName, settings.AdminPassword, "Administrator", settings.AdminEmail, Administrator: true),
                cancellationToken);
            LogAdminCreated(settings.AdminUserName, tenant.Identifier);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created tenant '{Tenant}'.")]
    private partial void LogTenantCreated(string tenant);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created administrator '{UserName}' in tenant '{Tenant}'.")]
    private partial void LogAdminCreated(string userName, string tenant);
}

/// <summary>Migrates the database and bootstraps before the server accepts requests.</summary>
internal sealed class StartupBootstrapService(IServiceProvider services, IOptions<DatabaseOptions> database) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (database.Value.MigrateOnStartup)
        {
            await services.GetRequiredService<PaperDotNet.Persistence.DatabaseMigrator>().MigrateAsync(cancellationToken);
        }

        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenantBootstrapper>().RunAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
