using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaperDotNet.Performance;

/// <summary>Repeatable API workloads. Not part of dotnet test; see this project's README.</summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try { options = Options.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        var report = new RunReport(2, DateTimeOffset.UtcNow, options, await MetadataAsync(options), []);
        try
        {
            foreach (var provider in options.Providers)
            {
                for (var repeat = 1; repeat <= options.Repeats; repeat++)
                {
                    var run = new ProviderRun(provider, repeat);
                    report.Runs.Add(run);
                    Console.WriteLine($"{provider} repeat {repeat}/{options.Repeats} ({(options.SeparateProcess ? "Kestrel process" : "in-process")})");
                    try
                    {
                        await WriteReportAsync(options.Output, report);
                        await RunProviderAsync(run, options, () => WriteReportAsync(options.Output, report), stop.Token);
                        run.Status = "completed";
                    }
                    catch (Exception ex)
                    {
                        run.Status = stop.IsCancellationRequested ? "cancelled" : "failed";
                        run.Failure = new FailureReport(run.Stage, ex.GetType().Name, ex.Message);
                        Console.Error.WriteLine($"{provider} repeat {repeat}: {run.Stage}: {ex.Message}");
                    }

                    await WriteReportAsync(options.Output, report);
                    if (stop.IsCancellationRequested) { break; }
                }

                if (stop.IsCancellationRequested) { break; }
            }
        }
        finally { Console.CancelKeyPress -= cancel; }

        report.ExitCode = ExitCode(report, options);
        await WriteReportAsync(options.Output, report);
        Console.WriteLine($"Wrote {Path.GetFullPath(options.Output)}; exit code {report.ExitCode}.");
        if (report.ExitCode == 2)
        {
            Console.Error.WriteLine("Gate failed: inspect request budgets and the optional RSS budget, including warm-up, phase and idle samples.");
        }
        return report.ExitCode.Value;
    }

    internal static int ExitCode(RunReport report, Options options)
    {
        if (report.Runs.Count != options.Providers.Count * options.Repeats || report.Runs.Any(r => r.Status != "completed" || r.Scenarios.Count != 6))
        {
            return 1;
        }

        var latencyFailed = report.Runs.Any(r => r.Scenarios.Any(s => s.Steps.Any(step => !step.WithinBudget)));
        var memoryFailed = options.MaxRssMiB is { } budget && report.Runs.Any(r =>
            r.MemoryPhases.Any(p => p.WorkingSetBytes > budget * 1024 * 1024)
            || r.IdleResources?.PeakSampledWorkingSetBytes > budget * 1024 * 1024
            || r.Scenarios.SelectMany(s => s.Steps).Concat(r.Warmups.Select(w => w.Step))
                .Any(step => step.ServerResources is null || step.ServerResources.PeakSampledWorkingSetBytes > budget * 1024 * 1024));
        return options.Gate && (latencyFailed || memoryFailed) ? 2 : 0;
    }

    private static async Task WriteReportAsync(string path, RunReport report)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, Json));
        File.Move(temporary, full, overwrite: true);
    }

    private static async Task<Dictionary<string, string>> MetadataAsync(Options options)
    {
        var metadata = new Dictionary<string, string>
        {
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["logicalProcessors"] = Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hosting"] = options.SeparateProcess ? "published Production Kestrel, loopback HTTP; no TLS/proxy" : "Testing WebApplicationFactory; shared client/server process",
            ["loadModel"] = "closed-loop concurrency; no arrival-rate capacity claim",
            ["resourceSampling"] = "250 ms OS CPU/RSS; database/child processes excluded; no managed allocation capture",
            ["backgroundSettings"] = "keyword search; scheduler interval 1 hour; durable outbox/indexing active; rate limit raised; OTLP export disabled",
            ["writeFixture"] = "create last; grows per step, including warm-up; fresh provider database per repeat",
        };
        foreach (var name in new[] { "DOTNET_PROCESSOR_COUNT", "DOTNET_GCHeapHardLimit", "DOTNET_GCHeapHardLimitPercent", "DOTNET_gcServer", "DOTNET_GCConserveMemory", "DOTNET_GCDynamicAdaptationMode", "DOTNET_GCDGen0GrowthPercent", "DOTNET_TieredCompilation", "DOTNET_TC_QuickJit", "DOTNET_TieredPGO", "TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value) { metadata[name] = value; }
        }
        var cgroupRoot = "/sys/fs/cgroup";
        if (File.Exists("/proc/self/cgroup"))
        {
            var cgroup = await File.ReadAllTextAsync("/proc/self/cgroup");
            metadata["cgroup"] = cgroup;
            var unified = cgroup.Split('\n').FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
            if (unified is not null) { cgroupRoot = Path.Combine(cgroupRoot, unified[3..].TrimStart('/')); }
        }
        foreach (var (key, path) in new[] { ("cpuMax", Path.Combine(cgroupRoot, "cpu.max")), ("memoryMax", Path.Combine(cgroupRoot, "memory.max")),
            ("memorySwapMax", Path.Combine(cgroupRoot, "memory.swap.max")), ("memoryInfo", "/proc/meminfo") })
        {
            if (File.Exists(path)) { metadata[key] = await File.ReadAllTextAsync(path); }
        }

        var start = new ProcessStartInfo("git") { WorkingDirectory = PerfHost.RepositoryRoot(), RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("HEAD");
        using (var git = Process.Start(start))
        {
            if (git is not null) { metadata["commit"] = (await git.StandardOutput.ReadToEndAsync()).Trim(); await git.WaitForExitAsync(); }
        }

        start.ArgumentList.Clear();
        start.ArgumentList.Add("status"); start.ArgumentList.Add("--porcelain");
        using (var git = Process.Start(start))
        {
            if (git is not null) { metadata["worktreeStatus"] = await git.StandardOutput.ReadToEndAsync(); await git.WaitForExitAsync(); }
        }

        metadata["runnerSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(Program).Assembly.Location)));

        if (options.HostPath is { } host && File.Exists(host))
        {
            metadata["hostSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(host)));
            var runtimeConfig = Path.ChangeExtension(host, ".runtimeconfig.json");
            if (File.Exists(runtimeConfig)) { metadata["hostRuntimeConfig"] = await File.ReadAllTextAsync(runtimeConfig); }
        }

        return metadata;
    }

    private static async Task RunProviderAsync(ProviderRun run, Options options, Func<Task> checkpoint, CancellationToken ct)
    {
        var startup = Stopwatch.StartNew();
        run.Stage = "provisioning/hosting";
        await using var host = await PerfHost.StartAsync(run.Provider, run.Repeat, options, stage => run.Stage = stage, ct);
        run.StartupSeconds = startup.Elapsed.TotalSeconds;
        run.Database = host.DatabaseMetadata;
        run.ServerLog = host.ServerLog;
        SnapshotMemory(run, host, "ready-before-seeding");
        run.Stage = "authentication/seeding";
        using var admin = await SignInAsync(host, PerfHost.UserName, PerfHost.Password, options.RequestTimeoutSeconds, ct);
        var world = await SeedAsync(admin, options.Items, ct);
        var shared = await SeedSharedAsync(admin, world.WorkspaceId, options.Items, ct);
        using var member = await SignInAsync(host, SharedWorld.MemberName, SharedWorld.MemberPassword, options.RequestTimeoutSeconds, ct);
        run.ExpectedIndexedDocuments = options.Items + shared.Documents + shared.Tasks;
        run.Stage = "index readiness";
        var indexing = Stopwatch.StartNew();
        await WaitForIndexAsync(admin, world, run.ExpectedIndexedDocuments, options.ReadyTimeoutSeconds, ct);
        run.IndexWaitSeconds = indexing.Elapsed.TotalSeconds;
        run.ReadySeconds = startup.Elapsed.TotalSeconds;
        Console.WriteLine($"Ready in {run.ReadySeconds:0.0}s; indexed {run.ExpectedIndexedDocuments} fixture documents.");
        SnapshotMemory(run, host, "indexed-fixture");

        // Read fixtures remain fixed; write/index growth cannot contaminate these comparisons.
        await MeasureAsync(run, "read", options, admin, (_, http, token) => ReadAsync(http, world, token), host, checkpoint, ct);
        await MeasureAsync(run, "query", options, admin, (_, http, token) => GetAsync(http, world.QueryUrl, token), host, checkpoint, ct);
        await MeasureAsync(run, "shared", options, member, (_, http, token) => GetAsync(http, shared.PageUrl, token), host, checkpoint, ct);
        await MeasureAsync(run, "mytasks", options, member, (_, http, token) => GetAsync(http, "/v1.0/me/tasks", token), host, checkpoint, ct);
        await MeasureAsync(run, "search", options, admin, (_, http, token) => GetAsync(http, world.SearchUrl, token), host, checkpoint, ct);
        await MeasureAsync(run, "create", options, admin, (worker, http, token) => CreateAsync(http, world, worker, token), host, checkpoint, ct,
            token => ItemCountAsync(admin, world, token));
        if (options.IdleSeconds > 0)
        {
            run.Stage = "idle observation";
            using var idle = new ResourceSampler(host.ServerProcess);
            await Task.Delay(TimeSpan.FromSeconds(options.IdleSeconds), ct);
            run.IdleResources = (await idle.CompleteAsync()).Server;
        }
        SnapshotMemory(run, host, "after-workload-and-idle");
        run.Stage = "cleanup";
    }

    private static void SnapshotMemory(ProviderRun run, PerfHost host, string phase)
    {
        if (host.ServerProcess is { } server)
        {
            server.Refresh();
            run.MemoryPhases.Add(new MemoryPhase(phase, server.WorkingSet64));
        }
    }

    private static async Task WaitForIndexAsync(HttpClient client, World world, int expected, double seconds, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var token = deadline.Token;
        var watch = Stopwatch.StartNew();
        var stable = 0;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            using var response = await client.GetAsync($"/v1.0/search?workspaceId={world.WorkspaceId}&$top=1", token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, token);
            stable = body.GetProperty("@odata.count").GetInt32() == expected ? stable + 1 : 0;
            if (stable >= 3)
            {
                using var alpha = await client.GetAsync(world.SearchUrl, token);
                alpha.EnsureSuccessStatusCode();
                var result = await alpha.Content.ReadFromJsonAsync<JsonElement>(Json, token);
                if (result.GetProperty("@odata.count").GetInt32() == world.AlphaItems) { return; }
                throw new InvalidOperationException("Indexed fixture has an unexpected alpha count.");
            }

            await Task.Delay(250, token);
        }

        throw new TimeoutException($"Search index did not reach {expected} documents within {seconds} seconds.");
    }

    private static async Task MeasureAsync(ProviderRun run, string name, Options options, HttpClient client,
        Func<int, HttpClient, CancellationToken, Task> operation, PerfHost host, Func<Task> checkpoint,
        CancellationToken ct, Func<CancellationToken, Task<int>>? fixtureCount = null)
    {
        run.Stage = $"scenario:{name}";
        var steps = new List<StepReport>();
        int? highest = null;
        var scenarioIndex = run.Scenarios.Count;
        run.Scenarios.Add(new ScenarioReport(run.Provider, run.Repeat, name, options.P95Ms, null, false, steps));
        foreach (var concurrency in Measurement.ConcurrencyLadder(options.MaxConcurrency))
        {
            if (options.WarmupSeconds > 0)
            {
                var warmup = await Measurement.RunAsync((worker, token) => operation(worker, client, token), concurrency,
                    options.WarmupSeconds, options, host.ServerProcess, ct);
                run.Warmups.Add(new WarmupReport(name, warmup));
                ct.ThrowIfCancellationRequested();
                if (warmup.Requests == 0 || warmup.Errors + warmup.Cancelled + warmup.TimedOut > 0)
                {
                    throw new InvalidOperationException($"{name} warm-up failed; see classified outcomes.");
                }
            }

            var before = fixtureCount is null ? (int?)null : await fixtureCount(ct);
            var step = await Measurement.RunAsync((worker, token) => operation(worker, client, token), concurrency,
                options.Seconds, options, host.ServerProcess, ct);
            var after = fixtureCount is null ? (int?)null : await fixtureCount(ct);
            step = step with { FixtureItemsBefore = before, FixtureItemsAfter = after };
            steps.Add(step);
            Console.WriteLine($"{name,-7} c={concurrency,2} {step.Requests,6} req {step.PerSecond,8:0.0} req/s "
                + $"p95 {step.SuccessLatency.P95Ms,8:0.0} ms errors={step.Errors} timeout={step.TimedOut} cancelled={step.Cancelled} "
                + (step.WithinBudget ? "pass" : "over budget"));
            if (step.WithinBudget) { highest = concurrency; }
            run.Scenarios[scenarioIndex] = new ScenarioReport(run.Provider, run.Repeat, name, options.P95Ms, highest,
                concurrency == options.MaxConcurrency && step.WithinBudget, steps);
            await checkpoint();
            ct.ThrowIfCancellationRequested();
            if (!step.WithinBudget) { break; }
        }
    }

    private static async Task<int> ItemCountAsync(HttpClient client, World world, CancellationToken ct)
    {
        using var response = await client.GetAsync($"/v1.0/workspaces/{world.WorkspaceId}/lists/{world.ListId}/items?$count=true&$top=1", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return body.GetProperty("@odata.count").GetInt32();
    }

    private static async Task<HttpClient> SignInAsync(PerfHost host, string userName, string password, double timeoutSeconds, CancellationToken ct)
    {
        var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        client.DefaultRequestHeaders.Add("X-Tenant", PerfHost.Tenant);
        try
        {
            using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "paperdotnet",
                ["username"] = userName,
                ["password"] = password,
                ["scope"] = "api",
            }), ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("access_token").GetString());
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task SendSeedAsync(HttpClient client, HttpMethod method, string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<World> SeedAsync(HttpClient client, int items, CancellationToken cancellationToken)
    {
        var workspace = await PostIdAsync(client, "/v1.0/workspaces", new { name = "Performance" }, cancellationToken);
        var list = await PostIdAsync(client, $"/v1.0/workspaces/{workspace}/lists", new { name = "Items" }, cancellationToken);
        var ids = new Guid[items];
        for (var i = 0; i < items; i++)
        {
            var word = i % 2 == 0 ? "alpha" : "beta";
            ids[i] = await PostIdAsync(
                client,
                $"/v1.0/workspaces/{workspace}/lists/{list}/items",
                new { fields = new { title = $"{word} note {i}" } },
                cancellationToken);
        }

        return new World(
            workspace,
            list,
            ids,
            $"/v1.0/workspaces/{workspace}/lists/{list}/items?$filter={Uri.EscapeDataString("contains(fields/title,'alpha')")}&$top=20",
            $"/v1.0/search?q=alpha&workspaceId={workspace}&$top=20",
            (items + 1) / 2);
    }

    /// <summary>
    /// A list like a document library with home folders: one folder per 4 items, each with unique permissions (owners
    /// only), every second one shared with a group the member is in. The member reads half of the folders.
    /// </summary>
    private static async Task<SharedWorld> SeedSharedAsync(HttpClient admin, Guid workspace, int items, CancellationToken cancellationToken)
    {
        var member = await PostIdAsync(admin, "/v1.0/users", new { userName = SharedWorld.MemberName, password = SharedWorld.MemberPassword }, cancellationToken);
        await SendSeedAsync(admin, HttpMethod.Post, $"/v1.0/workspaces/{workspace}/members", new { userId = member, role = "member" }, cancellationToken);
        var group = await PostIdAsync(admin, "/v1.0/groups", new { name = "Readers" }, cancellationToken);
        await SendSeedAsync(admin, HttpMethod.Post, $"/v1.0/groups/{group}/members", new { userId = member }, cancellationToken);

        var list = await PostIdAsync(admin, $"/v1.0/workspaces/{workspace}/lists", new { name = "Shared" }, cancellationToken);
        var itemsUrl = $"/v1.0/workspaces/{workspace}/lists/{list}/items";
        var folders = Math.Max(1, items / 4);
        for (var f = 0; f < folders; f++)
        {
            var folder = await PostIdAsync(admin, itemsUrl, new { isFolder = true, fields = new { title = $"folder {f}" } }, cancellationToken);
            await SendSeedAsync(admin, HttpMethod.Post, $"{itemsUrl}/{folder}/permissions/breakInheritance", new { copyGrants = false }, cancellationToken);
            if (f % 2 == 0)
            {
                var grants = new[] { new { principalType = "group", principalId = group, level = "read" } };
                await SendSeedAsync(admin, HttpMethod.Put, $"{itemsUrl}/{folder}/permissions/grants", new { grants }, cancellationToken);
            }

            for (var i = 0; i < 4; i++)
            {
                await PostIdAsync(admin, itemsUrl, new { parentId = folder, fields = new { title = $"document {f}.{i}" } }, cancellationToken);
            }
        }

        // Task lists for My tasks: one query across them all (ADR-0035 step 5).
        for (var l = 0; l < SharedWorld.TaskLists; l++)
        {
            var tasks = await PostIdAsync(admin, $"/v1.0/workspaces/{workspace}/lists", new { name = $"Tasks {l}", templateKey = "tasks" }, cancellationToken);
            for (var t = 0; t < Math.Max(1, items / 40); t++)
            {
                var assignedTo = t % 2 == 0 ? new[] { member } : [];
                await PostIdAsync(admin, $"/v1.0/workspaces/{workspace}/lists/{tasks}/items",
                    new { fields = new { title = $"task {l}.{t}", dueDate = $"2026-{1 + (t % 12):00}-{1 + (l % 28):00}", assignedTo } }, cancellationToken);
            }
        }

        return new SharedWorld(folders, $"{itemsUrl}?$top=20", folders * 4, SharedWorld.TaskLists * Math.Max(1, items / 40));
    }

    private static async Task CreateAsync(HttpClient http, World world, int worker, CancellationToken cancellationToken)
    {
        var title = $"created {worker} {Guid.NewGuid():N}";
        using var response = await http.PostAsJsonAsync(
            $"/v1.0/workspaces/{world.WorkspaceId}/lists/{world.ListId}/items",
            new { fields = new { title } },
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RequestFailure(response.StatusCode);
        }

        await response.Content.CopyToAsync(Stream.Null, cancellationToken);
    }

    private static Task ReadAsync(HttpClient http, World world, CancellationToken cancellationToken)
    {
        var id = world.Ids[Random.Shared.Next(world.Ids.Length)];
        return GetAsync(http, $"/v1.0/workspaces/{world.WorkspaceId}/lists/{world.ListId}/items/{id}", cancellationToken);
    }

    private static async Task GetAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RequestFailure(response.StatusCode);
        }

        await response.Content.CopyToAsync(Stream.Null, cancellationToken);
    }

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(url, body, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {url}: {json}");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetGuid();
    }

}

internal sealed record World(Guid WorkspaceId, Guid ListId, Guid[] Ids, string QueryUrl, string SearchUrl, int AlphaItems);
internal sealed record SharedWorld(int Folders, string PageUrl, int Documents, int Tasks)
{
    public const string MemberName = "member";
    public const string MemberPassword = "perf-member-password-1";
    public const int TaskLists = 20;
}
internal sealed record RunReport(int SchemaVersion, DateTimeOffset StartedUtc, Options Options,
    IReadOnlyDictionary<string, string> Metadata, List<ProviderRun> Runs)
{
    public int? ExitCode { get; set; }
}
internal sealed record FailureReport(string Stage, string Type, string Message);
internal sealed record WarmupReport(string Scenario, StepReport Step);
internal sealed record MemoryPhase(string Phase, long WorkingSetBytes);
internal sealed class ProviderRun(string provider, int repeat)
{
    public string Provider { get; } = provider;
    public int Repeat { get; } = repeat;
    public string Status { get; set; } = "running";
    public string Stage { get; set; } = "provisioning";
    public FailureReport? Failure { get; set; }
    public DatabaseMetadata? Database { get; set; }
    public string? ServerLog { get; set; }
    public double StartupSeconds { get; set; }
    public double ReadySeconds { get; set; }
    public double IndexWaitSeconds { get; set; }
    public int ExpectedIndexedDocuments { get; set; }
    public List<WarmupReport> Warmups { get; } = [];
    public List<MemoryPhase> MemoryPhases { get; } = [];
    public ResourceReport? IdleResources { get; set; }
    public List<ScenarioReport> Scenarios { get; } = [];
}
