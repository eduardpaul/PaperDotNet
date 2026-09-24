using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Host;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Extensions.Testing;

/// <summary>How <see cref="ExtensionTestHost"/> runs the host.</summary>
public sealed class ExtensionTestHostOptions
{
    /// <summary><c>Sqlite</c> (default: a temporary file, deleted on dispose) or <c>PostgreSql</c>.</summary>
    public string DatabaseProvider { get; set; } = "Sqlite";

    /// <summary>Required for PostgreSQL; for SQLite null means a temporary file.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Password of the administrator created in every test tenant.</summary>
    public string AdminPassword { get; set; } = "test-admin-password";

    /// <summary>Further host configuration (e.g. <c>Jobs:SchedulerInterval</c>).</summary>
    public IDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>
/// Runs the real PaperDotNet host in-process with the extensions under test (EXT-05): migrations,
/// modules, authentication and the extension runtime as in production. Create tenants with
/// <see cref="CreateTenantAsync"/> (each has an administrator and the extensions enabled) and
/// call the API with their clients, or run code inside a tenant with <see cref="TestTenant.RunAsync"/>.
/// Share one host per test run (fixture) and isolate tests by tenant: the host starts once.
/// </summary>
/// <remarks>Extensions are added to <see cref="PaperDotNetHost.AdditionalExtensions"/>, which is process-wide.</remarks>
public class ExtensionTestHost : WebApplicationFactory<Program>
{
    public const string AdminUserName = "admin";

    private readonly ExtensionTestHostOptions _options;
    private readonly IReadOnlyList<IExtension> _extensions;
    private readonly string? _sqliteFile;
    private readonly string _connectionString;
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"pdn_ext_test_data_{Ids.New():N}");
    private int _tenants;

    public ExtensionTestHost(params IExtension[] extensions)
        : this(new ExtensionTestHostOptions(), extensions)
    {
    }

    public ExtensionTestHost(ExtensionTestHostOptions options, params IExtension[] extensions)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _extensions = extensions;
        if (options.ConnectionString is { Length: > 0 } connectionString)
        {
            _connectionString = connectionString;
        }
        else if (string.Equals(options.DatabaseProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            _sqliteFile = Path.Combine(Path.GetTempPath(), $"pdn_ext_test_{Ids.New():N}.db");
            _connectionString = $"Data Source={_sqliteFile}";
        }
        else
        {
            throw new ArgumentException("A connection string is required for PostgreSQL.", nameof(options));
        }

        lock (PaperDotNetHost.AdditionalExtensions)
        {
            foreach (var extension in extensions.Where(e => !PaperDotNetHost.AdditionalExtensions.Any(a => a.GetType() == e.GetType())))
            {
                PaperDotNetHost.AdditionalExtensions.Add(extension);
            }
        }
    }

    public ExtensionTestHostOptions Options => _options;

    /// <summary>
    /// A new tenant with an administrator (<see cref="AdminUserName"/>) and, by default, the
    /// extensions under test enabled.
    /// </summary>
    public async Task<TestTenant> CreateTenantAsync(string? identifier = null, bool enableExtensions = true, CancellationToken cancellationToken = default)
    {
        identifier ??= $"test-{Interlocked.Increment(ref _tenants)}-{Ids.New():N}"[..24];
        await using var scope = Services.CreateAsyncScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().CreateAsync(identifier, identifier, [], cancellationToken);
        Guid adminId;
        await using (var tenantScope = Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            adminId = await tenantScope.ServiceProvider.GetRequiredService<IUserDirectory>()
                .CreateUserAsync(new NewUser(AdminUserName, _options.AdminPassword, Administrator: true), cancellationToken);
        }

        var result = new TestTenant(this, tenant.Id, tenant.Identifier, adminId);
        if (enableExtensions)
        {
            using var admin = await result.CreateClientAsync(cancellationToken: cancellationToken);
            foreach (var extension in _extensions)
            {
                await TestTenant.EnableAsync(admin, ExtensionId(extension), cancellationToken);
            }
        }

        return result;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", _options.DatabaseProvider);
        builder.UseSetting("ConnectionStrings:PaperDotNet", _connectionString);
        builder.UseSetting("Auth:RequireHttps", "false");
        builder.UseSetting("Tenancy:AllowHeader", "true");
        builder.UseSetting("Bootstrap:AdminPassword", _options.AdminPassword);
        builder.UseSetting("Jobs:SchedulerInterval", "00:00:01");
        builder.UseSetting("Storage:DataPath", _dataPath);
        foreach (var (key, value) in _options.Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }

        if (_sqliteFile is not null)
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _sqliteFile, _sqliteFile + "-wal", _sqliteFile + "-shm" }.Where(File.Exists))
            {
                File.Delete(file);
            }
        }
    }

    private static string ExtensionId(IExtension extension)
    {
        using var stream = extension.GetType().Assembly.GetManifestResourceStream(ExtensionSdk.ManifestResource)
            ?? throw new InvalidOperationException($"Extension {extension.GetType().FullName} has no embedded manifest.");
        return ExtensionManifest.Parse(stream).Id;
    }
}

/// <summary>A tenant created by <see cref="ExtensionTestHost.CreateTenantAsync"/>.</summary>
public sealed class TestTenant
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ExtensionTestHost _host;

    internal TestTenant(ExtensionTestHost host, Guid id, string identifier, Guid adminUserId)
    {
        _host = host;
        Id = id;
        Identifier = identifier;
        AdminUserId = adminUserId;
    }

    public Guid Id { get; }

    public string Identifier { get; }

    public Guid AdminUserId { get; }

    /// <summary>An API client for this tenant, signed in as <paramref name="userName"/> (default: the administrator).</summary>
    public async Task<HttpClient> CreateClientAsync(string userName = ExtensionTestHost.AdminUserName, string? password = null, CancellationToken cancellationToken = default)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", Identifier);
        using var response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "paperdotnet",
            ["username"] = userName,
            ["password"] = password ?? _host.Options.AdminPassword,
            ["scope"] = "api offline_access",
        }), cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken)).GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Creates a user (a member unless <paramref name="administrator"/>) and returns a client signed in as them.</summary>
    public async Task<HttpClient> CreateUserAsync(string userName, bool administrator = false, CancellationToken cancellationToken = default)
    {
        var password = $"pw-{Ids.New():N}";
        await RunAsync(services => services.GetRequiredService<IUserDirectory>()
            .CreateUserAsync(new NewUser(userName, password, Administrator: administrator), cancellationToken), cancellationToken: cancellationToken);
        return await CreateClientAsync(userName, password, cancellationToken);
    }

    public async Task EnableAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        using var admin = await CreateClientAsync(cancellationToken: cancellationToken);
        await EnableAsync(admin, extensionId, cancellationToken);
    }

    public async Task DisableAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        using var admin = await CreateClientAsync(cancellationToken: cancellationToken);
        using var response = await admin.PostAsync(new Uri($"/v1.0/extensions/{extensionId}/disable", UriKind.Relative), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Replaces the extension's settings in this tenant (validated against the manifest).</summary>
    public async Task ConfigureAsync(string extensionId, object settings, CancellationToken cancellationToken = default)
    {
        using var admin = await CreateClientAsync(cancellationToken: cancellationToken);
        using var response = await admin.PutAsJsonAsync($"/v1.0/extensions/{extensionId}/settings", settings, Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Settings were rejected: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> in a service scope of this tenant, acting as the administrator
    /// (or <paramref name="userId"/>), e.g. to call <c>IListItemStore</c> or the extension's services.
    /// </summary>
    public Task RunAsync(Func<IServiceProvider, Task> action, Guid? userId = null, CancellationToken cancellationToken = default) =>
        RunAsync<object?>(async services =>
        {
            await action(services);
            return null;
        }, userId, cancellationToken);

    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> action, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = _host.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(Id, Identifier, userId ?? AdminUserId);
        return await action(scope.ServiceProvider);
    }

    internal static async Task EnableAsync(HttpClient admin, string extensionId, CancellationToken cancellationToken)
    {
        using var response = await admin.PostAsync(new Uri($"/v1.0/extensions/{extensionId}/enable", UriKind.Relative), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
