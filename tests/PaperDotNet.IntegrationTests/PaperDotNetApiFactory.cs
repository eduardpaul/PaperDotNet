using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PaperDotNet.Abstractions;
using PaperDotNet.Host;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Messaging;
using PaperDotNet.Tenancy.Contracts;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PaperDotNet.IntegrationTests.PaperDotNetApiFactory))]

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Runs the real app against the database chosen by PAPERDOTNET_TEST_PROVIDER:
/// <c>sqlite</c> (default, a temporary file) or <c>postgresql</c> (the server in
/// PAPERDOTNET_TEST_POSTGRES, or a Testcontainers instance). Each test run gets
/// its own database; tests isolate by creating tenants.
/// </summary>
public sealed class PaperDotNetApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminUserName = "admin";
    public const string AdminPassword = "integration-admin-password";
    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;
    private string? _sqliteFile;

    /// <summary>PostgreSQL only: the test database with the server's admin credentials.</summary>
    public string? AdminConnectionString { get; private set; }

    /// <summary>The connection string the app uses.</summary>
    public string ConnectionString => _connectionString;

    public static string Provider { get; } =
        Environment.GetEnvironmentVariable("PAPERDOTNET_TEST_PROVIDER") is { Length: > 0 } p ? p.ToLowerInvariant() : "sqlite";

    public async ValueTask InitializeAsync()
    {
        if (Provider == "sqlite")
        {
            _sqliteFile = Path.Combine(Path.GetTempPath(), $"pdn_test_{Guid.NewGuid():N}.db");
            _connectionString = $"Data Source={_sqliteFile}";
        }
        else
        {
            var server = Environment.GetEnvironmentVariable("PAPERDOTNET_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(server))
            {
                _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
                await _container.StartAsync();
                server = _container.GetConnectionString();
            }

            // The app runs as an ordinary role that owns its database (like production), so
            // row-level security applies to every test; superusers would bypass it.
            var suffix = Guid.NewGuid().ToString("N");
            var role = $"pdn_app_{suffix[..12]}";
            var password = $"pw_{suffix}";
            var database = $"pdn_test_{suffix}";
            await using (var admin = new NpgsqlConnection(server))
            {
                await admin.OpenAsync();
                foreach (var sql in new[] { $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE", $"CREATE DATABASE {database} OWNER {role}" })
                {
                    await using var command = new NpgsqlCommand(sql, admin);
                    await command.ExecuteNonQueryAsync();
                }
            }

            AdminConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;
            _connectionString = new NpgsqlConnectionStringBuilder(server) { Database = database, Username = role, Password = password }.ConnectionString;
        }

        // The sample extension, as a custom host build would reference it.
        if (!PaperDotNetHost.AdditionalExtensions.Any(e => e is PaperDotNet.Samples.Invoices.InvoicesExtension))
        {
            PaperDotNetHost.AdditionalExtensions.Add(new PaperDotNet.Samples.Invoices.InvoicesExtension());
        }

        // Start the host now so migrations and bootstrap run once.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", Provider == "sqlite" ? "Sqlite" : "PostgreSql");
        builder.UseSetting("ConnectionStrings:PaperDotNet", _connectionString);
        builder.UseSetting("Auth:RequireHttps", "false");
        builder.UseSetting("Auth:PasskeyOrigins:0", "http://localhost");
        builder.UseSetting("Tenancy:AllowHeader", "true");
        builder.UseSetting("Bootstrap:AdminPassword", AdminPassword);
        builder.UseSetting("Jobs:SchedulerInterval", "00:00:01");
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IItemEventReceiver, TestReceiver>();
            services.AddEventSubscriber<ItemAdded, TestSubscriber>();
            services.AddEventSubscriber<ItemUpdated, TestSubscriber>();
            services.AddTenantRecurringJob<TestRecurringJob>("test.every-second", "* * * * * *");
        });
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

        if (_sqliteFile is not null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _sqliteFile, _sqliteFile + "-wal", _sqliteFile + "-shm" }.Where(File.Exists))
            {
                File.Delete(file);
            }
        }
    }
}
