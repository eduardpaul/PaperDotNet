using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Host;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Features;

namespace PaperDotNet.Extensions.Testing;

/// <summary>How <see cref="ExtensionTestHost"/> runs the host.</summary>
public sealed class ExtensionTestHostOptions
{
    /// <summary>Password of the administrator created in every test tenant.</summary>
    public string AdminPassword { get; set; } = "test-admin-password";

    /// <summary>Further host configuration (e.g. <c>Jobs:SchedulerInterval</c>).</summary>
    public IDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>
/// Runs the real PaperDotNet host in-process with the extensions under test (EXT-05) on a temporary SQLite database
/// (the database of the Native AOT build, ADR-0039): migrations, modules, authentication and the extension runtime as
/// in production. Create tenants with
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
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"pdn_ext_test_{Ids.New():N}");
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
        Guid tenantId;
        Guid adminId;
        await using (var scope = Services.CreateAsyncScope())
        {
            var (tenant, admin) = await scope.ServiceProvider.GetRequiredService<TenantProvisioner>()
                .CreateAsync(identifier, identifier, AdminUserName, _options.AdminPassword, cancellationToken);
            (tenantId, adminId) = (tenant.Id, admin.Id);
        }

        var result = new TestTenant(this, tenantId, identifier, adminId);
        if (enableExtensions)
        {
            using var client = await result.CreateClientAsync(cancellationToken: cancellationToken);
            foreach (var extension in _extensions)
            {
                await TestTenant.EnableAsync(client, ExtensionId(extension), cancellationToken);
            }
        }

        return result;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:DataPath", _dataPath);
        builder.UseSetting("Jobs:SchedulerInterval", "00:00:00.200");

        // One host serves every test of a run: sign-ins of all of them count against one address.
        builder.UseSetting("Identity:SignInsPerMinute", "1000");
        foreach (var (key, value) in _options.Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
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
        using var response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = userName,
            ["password"] = password ?? _host.Options.AdminPassword,
            ["tenant"] = Identifier,
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
            .CreateUserAsync(Id, new NewUser(userName, password, Administrator: administrator), cancellationToken), cancellationToken);
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

    /// <summary>The administrator as the author of changes, e.g. for <c>IListItemStore.ActingAs</c> or <c>AsSystem</c>.</summary>
    public ChangeActor Admin => new(Id, AdminUserId);

    /// <summary>
    /// Runs <paramref name="action"/> in a service scope, e.g. to call <c>IListItemStore</c> or the extension's
    /// services. There is no ambient tenant (ADR-0039): name it, e.g. <c>items.ActingAs(tenant.Admin)</c>.
    /// </summary>
    public Task RunAsync(Func<IServiceProvider, Task> action, CancellationToken cancellationToken = default) =>
        RunAsync<object?>(async services =>
        {
            await action(services);
            return null;
        }, cancellationToken);

    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = _host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    internal static async Task EnableAsync(HttpClient admin, string extensionId, CancellationToken cancellationToken)
    {
        using var response = await admin.PostAsync(new Uri($"/v1.0/extensions/{extensionId}/enable", UriKind.Relative), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
