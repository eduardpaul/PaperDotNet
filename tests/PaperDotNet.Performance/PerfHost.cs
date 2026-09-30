using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

[assembly: WebApplicationFactoryContentRoot(
    "paperdotnet, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null",
    "../../../../../src/PaperDotNet.Host", "PaperDotNet.Host.csproj", "0")]

namespace PaperDotNet.Performance;

internal sealed class PerfHost : IAsyncDisposable
{
    public const string Tenant = "default";
    public const string UserName = "admin";
    public const string Password = "perf-admin-password";
    private readonly PerfDatabase database;
    private InProcessHost? factory;
    private Process? process;
    private Task logs = Task.CompletedTask;
    private Uri? address;

    private PerfHost(PerfDatabase database) => this.database = database;
    public Process? ServerProcess => process;
    public DatabaseMetadata DatabaseMetadata => database.Metadata;
    public string? ServerLog { get; private set; }

    public static async Task<PerfHost> StartAsync(string provider, int repeat, Options options, Action<string> stage, CancellationToken ct)
    {
        stage("provisioning");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(TimeSpan.FromSeconds(options.ReadyTimeoutSeconds));
        var database = await PerfDatabase.CreateAsync(provider, startup.Token);
        var host = new PerfHost(database);
        try
        {
            var settings = Settings(provider, database);
            stage("hosting");
            if (options.SeparateProcess)
            {
                await host.StartProcessAsync(settings, provider, repeat, options, startup.Token);
            }
            else
            {
                host.factory = new InProcessHost(settings);
                _ = host.factory.Server;
            }

            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateClient() => factory is not null ? factory.CreateClient() : new HttpClient { BaseAddress = address };

    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaperDotNet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static Dictionary<string, string> Settings(string provider, PerfDatabase database) => new(StringComparer.Ordinal)
    {
        ["Database:Provider"] = provider == "sqlite" ? "Sqlite" : "PostgreSql",
        ["ConnectionStrings:PaperDotNet"] = database.ConnectionString,
        ["Database:MigrateOnStartup"] = "true",
        ["Auth:RequireHttps"] = "false",
        ["Auth:AllowPasswordGrant"] = "true",
        ["Auth:AccessTokenLifetime"] = "1.00:00:00",
        ["Tenancy:AllowHeader"] = "true",
        ["Tenancy:DefaultTenant"] = Tenant,
        ["Bootstrap:TenantIdentifier"] = Tenant,
        ["Bootstrap:TenantName"] = "Performance",
        ["Bootstrap:AdminUserName"] = UserName,
        ["Bootstrap:AdminPassword"] = Password,
        ["Storage:DataPath"] = database.DataPath,
        ["Jobs:SchedulerInterval"] = "01:00:00",
        ["Search:DefaultMode"] = "Keyword",
        ["Messaging:RuntimeCompilation"] = Environment.GetEnvironmentVariable("PERF_RUNTIME_COMPILATION") ?? "false",
        ["RateLimit:PermitPerMinute"] = "1000000000",
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
        ["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Information",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
    };

    private async Task StartProcessAsync(Dictionary<string, string> settings, string provider, int repeat, Options options, CancellationToken ct)
    {
        var path = options.HostPath is { } configured ? Path.GetFullPath(configured)
            : throw new ArgumentException("Process mode requires PERF_HOST_PATH pointing to a published paperdotnet.dll. See tests/PaperDotNet.Performance/README.md.");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Published host not found.", path);
        }

        ServerLog = Path.GetFullPath(options.Output) + $".{provider}.repeat-{repeat}.server.log";
        Directory.CreateDirectory(Path.GetDirectoryName(ServerLog)!);
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(path);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("PAPERDOTNET__", StringComparison.Ordinal)
            || k.StartsWith("OTEL_", StringComparison.Ordinal)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (key, value) in settings)
        {
            start.Environment["PAPERDOTNET__" + key.Replace(":", "__", StringComparison.Ordinal)] = value;
        }

        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start host.");
        var writer = new StreamWriter(ServerLog) { AutoFlush = true };
        var logGate = new SemaphoreSlim(1, 1);
        async Task PumpAsync(StreamReader reader)
        {
            // Drain logs through teardown even when the workload is cancelled.
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                await logGate.WaitAsync(CancellationToken.None);
                try { await writer.WriteLineAsync(line); }
                finally { logGate.Release(); }
                const string prefix = "Now listening on: ";
                var index = line.IndexOf(prefix, StringComparison.Ordinal);
                if (index >= 0 && Uri.TryCreate(line[(index + prefix.Length)..].Trim(), UriKind.Absolute, out var url))
                {
                    listening.TrySetResult(url);
                }
            }
        }

        async Task CompleteLogsAsync()
        {
            try { await Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError)); }
            finally { await writer.DisposeAsync(); logGate.Dispose(); }
        }

        logs = CompleteLogsAsync();
        var exited = process.WaitForExitAsync(ct);
        var ready = listening.Task.WaitAsync(TimeSpan.FromSeconds(options.ReadyTimeoutSeconds), ct);
        if (await Task.WhenAny(ready, exited) == exited)
        {
            throw new InvalidOperationException($"Host exited before listening. See {ServerLog}.");
        }

        address = await ready;
        using var client = CreateClient();
        using var response = await client.GetAsync("/health/ready", ct);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (factory is not null) { await factory.DisposeAsync(); }
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                await process.WaitForExitAsync();
                await logs;
                process.Dispose();
            }
        }
        finally { await database.DisposeAsync(); }
    }

    private sealed class InProcessHost(Dictionary<string, string> settings) : WebApplicationFactory<global::Program>
    {
        private static string ContentRoot => Path.Combine(RepositoryRoot(), "src", "PaperDotNet.Host");
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseContentRoot(ContentRoot);
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ContentRoot).UseEnvironment("Testing");
            foreach (var (key, value) in settings) { builder.UseSetting(key, value); }
        }
    }
}
