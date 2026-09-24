using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Tenancy.Contracts;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PaperDotNet.IntegrationTests.PaperDotNetApiFactory))]

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Runs the real app against PostgreSQL: a Testcontainers instance, or the
/// server in PAPERDOTNET_TEST_POSTGRES when set (e.g. a local Postgres).
/// Each test run gets its own database; tests isolate by creating tenants.
/// </summary>
public sealed class PaperDotNetApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminUserName = "admin";
    public const string AdminPassword = "integration-admin-password";
    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("PAPERDOTNET_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(server))
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            server = _container.GetConnectionString();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(server) { Database = $"pdn_test_{Guid.NewGuid():N}" }.ConnectionString;

        // Start the host now so migrations and bootstrap run once.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:PaperDotNet", _connectionString);
        builder.UseSetting("Auth:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.UseSetting("Tenancy:AllowHeader", "true");
        builder.UseSetting("Bootstrap:AdminPassword", AdminPassword);
    }

    /// <summary>Creates a new tenant with its own administrator.</summary>
    public async Task<TenantSummary> CreateTenantAsync(string identifier)
    {
        await using var scope = Services.CreateAsyncScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().CreateAsync(identifier, identifier, [], CancellationToken.None);
        await using var tenantScope = Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        await tenantScope.ServiceProvider.GetRequiredService<IUserDirectory>()
            .CreateUserAsync(new NewUser(AdminUserName, AdminPassword, Administrator: true), CancellationToken.None);
        return tenant;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
