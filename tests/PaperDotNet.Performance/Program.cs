using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaperDotNet.Performance;

/// <summary>
/// Measures the API against SQLite and/or PostgreSQL and ramps concurrency until the run misses its limit.
/// Not part of <c>dotnet test</c>. See the repository README.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        var report = new List<ScenarioReport>();
        foreach (var provider in options.Providers)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {provider} ===");
            try
            {
                report.AddRange(await RunProviderAsync(provider, options, CancellationToken.None));
            }
            catch (Exception ex) when (provider == "postgresql")
            {
                Console.Error.WriteLine($"PostgreSQL was not measured: {ex.Message}");
                Console.Error.WriteLine("Start Docker, or set PAPERDOTNET_TEST_POSTGRES to a server the runner may create a database on.");
                if (options.Providers.Count == 1)
                {
                    return 1;
                }
            }
        }

        var path = Path.GetFullPath(options.Output);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json));
        Console.WriteLine();
        Console.WriteLine($"Wrote {path}");
        return report.Count == 0 ? 1 : 0;
    }

    private static async Task<List<ScenarioReport>> RunProviderAsync(string provider, Options options, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        await using var host = await PerfHost.StartAsync(provider, cancellationToken);
        using var client = await SignInAsync(host, PerfHost.UserName, PerfHost.Password, cancellationToken);
        var world = await SeedAsync(client, options.Items, cancellationToken);
        var shared = await SeedSharedAsync(client, world.WorkspaceId, options.Items, cancellationToken);
        using var member = await SignInAsync(host, SharedWorld.MemberName, SharedWorld.MemberPassword, cancellationToken);
        Console.WriteLine(
            $"Ready in {started.Elapsed.TotalSeconds:0.0}s. {options.Items} items, search indexed in {world.IndexSeconds:0.0}s; "
            + $"{shared.Folders} folders with unique permissions, half shared with the member's group; {SharedWorld.TaskLists} task lists.");

        var reports = new List<ScenarioReport>();
        reports.Add(await MeasureAsync(provider, "create", options, client, (worker, http, ct) => CreateAsync(http, world, worker, ct), cancellationToken));
        reports.Add(await MeasureAsync(provider, "read", options, client, (_, http, ct) => ReadAsync(http, world, ct), cancellationToken));
        reports.Add(await MeasureAsync(provider, "query", options, client, (_, http, ct) => GetAsync(http, world.QueryUrl, ct), cancellationToken));

        // A member without full control: every request resolves their principals and allowed scopes (ADR-0035).
        reports.Add(await MeasureAsync(provider, "shared", options, member, (_, http, ct) => GetAsync(http, shared.PageUrl, ct), cancellationToken));
        reports.Add(await MeasureAsync(provider, "mytasks", options, member, (_, http, ct) => GetAsync(http, "/v1.0/me/tasks", ct), cancellationToken));
        if (world.SearchReady)
        {
            reports.Add(await MeasureAsync(provider, "search", options, client, (_, http, ct) => GetAsync(http, world.SearchUrl, ct), cancellationToken));
        }
        else
        {
            Console.WriteLine("search: skipped (the index did not catch up before the deadline).");
        }

        return reports;
    }

    private static async Task<ScenarioReport> MeasureAsync(
        string provider, string name, Options options, HttpClient client, Func<int, HttpClient, CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        var steps = new List<StepReport>();
        int? limit = null;
        foreach (var concurrency in ConcurrencyLadder(options.MaxConcurrency))
        {
            var step = await RunStepAsync(client, operation, concurrency, options.Seconds, cancellationToken);
            steps.Add(step);
            var within = step.Requests > 0 && step.Errors == 0 && step.P95Ms <= options.P95Ms;
            Console.WriteLine(
                $"{name,-7} c={concurrency,2}  {step.Requests,6} req  {step.PerSecond,7:0.0} req/s  "
                + $"p50 {step.P50Ms,7:0.0}  p95 {step.P95Ms,7:0.0}  p99 {step.P99Ms,7:0.0} ms"
                + (step.Errors > 0 ? $"  errors {step.Errors}" : string.Empty)
                + (within ? string.Empty : "  over limit"));
            if (!within)
            {
                break;
            }

            limit = concurrency;
        }

        Console.WriteLine(limit is { } held
            ? $"{name}: holds up to {held} concurrent caller(s) under p95 {options.P95Ms:0} ms with no errors."
            : $"{name}: does not hold even 1 caller under p95 {options.P95Ms:0} ms with no errors.");
        return new ScenarioReport(provider, name, options.P95Ms, limit, steps);
    }

    private static async Task<StepReport> RunStepAsync(
        HttpClient client, Func<int, HttpClient, CancellationToken, Task> operation, int concurrency, double seconds, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        var latencies = new List<double>(capacity: 1024);
        var errors = 0;
        var gate = new object();
        var workers = Enumerable.Range(0, concurrency).Select(async worker =>
        {
            while (!stop.IsCancellationRequested)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await operation(worker, client, stop.Token);
                    var elapsed = watch.Elapsed.TotalMilliseconds;
                    lock (gate)
                    {
                        latencies.Add(elapsed);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });
        var elapsedWatch = Stopwatch.StartNew();
        await Task.WhenAll(workers);
        elapsedWatch.Stop();
        latencies.Sort();
        var duration = Math.Max(elapsedWatch.Elapsed.TotalSeconds, 0.001);
        return new StepReport(concurrency, latencies.Count, errors, latencies.Count / duration, Percentile(latencies, 0.50), Percentile(latencies, 0.95), Percentile(latencies, 0.99));
    }

    private static async Task<HttpClient> SignInAsync(PerfHost host, string userName, string password, CancellationToken cancellationToken)
    {
        var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-Tenant", PerfHost.Tenant);
        using var response = await client.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "paperdotnet",
                ["username"] = userName,
                ["password"] = password,
                ["scope"] = "api",
            }),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("access_token").GetString());
        return client;
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

        var index = Stopwatch.StartNew();
        var ready = false;
        while (index.Elapsed < TimeSpan.FromSeconds(30))
        {
            using var response = await client.GetAsync($"/v1.0/search?q=alpha&workspaceId={workspace}&$top=1", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
                if (body.GetProperty("@odata.count").GetInt32() > 0)
                {
                    ready = true;
                    break;
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return new World(
            workspace,
            list,
            ids,
            $"/v1.0/workspaces/{workspace}/lists/{list}/items?$filter={Uri.EscapeDataString("contains(fields/title,'alpha')")}&$top=20",
            $"/v1.0/search?q=alpha&workspaceId={workspace}&$top=20",
            ready,
            index.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// A list like a document library with home folders: one folder per 4 items, each with unique permissions (owners
    /// only), every second one shared with a group the member is in. The member reads half of the folders.
    /// </summary>
    private static async Task<SharedWorld> SeedSharedAsync(HttpClient admin, Guid workspace, int items, CancellationToken cancellationToken)
    {
        var member = await PostIdAsync(admin, "/v1.0/users", new { userName = SharedWorld.MemberName, password = SharedWorld.MemberPassword }, cancellationToken);
        (await admin.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/members", new { userId = member, role = "member" }, cancellationToken)).EnsureSuccessStatusCode();
        var group = await PostIdAsync(admin, "/v1.0/groups", new { name = "Readers" }, cancellationToken);
        (await admin.PostAsJsonAsync($"/v1.0/groups/{group}/members", new { userId = member }, cancellationToken)).EnsureSuccessStatusCode();

        var list = await PostIdAsync(admin, $"/v1.0/workspaces/{workspace}/lists", new { name = "Shared" }, cancellationToken);
        var itemsUrl = $"/v1.0/workspaces/{workspace}/lists/{list}/items";
        var folders = Math.Max(1, items / 4);
        for (var f = 0; f < folders; f++)
        {
            var folder = await PostIdAsync(admin, itemsUrl, new { isFolder = true, fields = new { title = $"folder {f}" } }, cancellationToken);
            (await admin.PostAsJsonAsync($"{itemsUrl}/{folder}/permissions/breakInheritance", new { copyGrants = false }, cancellationToken)).EnsureSuccessStatusCode();
            if (f % 2 == 0)
            {
                var grants = new[] { new { principalType = "group", principalId = group, level = "read" } };
                (await admin.PutAsJsonAsync($"{itemsUrl}/{folder}/permissions/grants", new { grants }, cancellationToken)).EnsureSuccessStatusCode();
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

        return new SharedWorld(folders, $"{itemsUrl}?$top=20");
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
            throw new HttpRequestException(((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _ = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static Task ReadAsync(HttpClient http, World world, CancellationToken cancellationToken)
    {
        var id = world.Ids[Random.Shared.Next(world.Ids.Length)];
        return GetAsync(http, $"/v1.0/workspaces/{world.WorkspaceId}/lists/{world.ListId}/items/{id}", cancellationToken);
    }

    private static async Task GetAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _ = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task<Guid> PostIdAsync(HttpClient client, string url, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(url, body, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {url}: {json}");
        }

        return JsonDocument.Parse(json).RootElement.GetProperty("id").GetGuid();
    }

    private static IEnumerable<int> ConcurrencyLadder(int max)
    {
        for (var concurrency = 1; concurrency <= max; concurrency *= 2)
        {
            yield return concurrency;
        }
    }

    private static double Percentile(List<double> sorted, double rank)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (sorted.Count - 1) * rank;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return lower == upper ? sorted[lower] : sorted[lower] + ((sorted[upper] - sorted[lower]) * (index - lower));
    }
}

internal sealed record Options(IReadOnlyList<string> Providers, int Items, double Seconds, int MaxConcurrency, double P95Ms, string Output)
{
    public static Options Parse(string[] args)
    {
        var smoke = args.Contains("--smoke");
        var providers = args.Where(a => a is "sqlite" or "postgresql" or "both").ToList();
        if (providers.Count == 0)
        {
            providers = ["sqlite"];
        }
        else if (providers.Contains("both"))
        {
            providers = ["sqlite", "postgresql"];
        }

        static int Number(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

        static double Seconds(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

        return new Options(
            providers,
            smoke ? 20 : Number("PERF_ITEMS", 200),
            smoke ? 1 : Seconds("PERF_SECONDS", 3),
            smoke ? 1 : Number("PERF_MAX_CONCURRENCY", 16),
            Seconds("PERF_P95_MS", 1000),
            Environment.GetEnvironmentVariable("PERF_OUTPUT") is { Length: > 0 } output ? output : "perf-results.json");
    }
}

internal sealed record World(Guid WorkspaceId, Guid ListId, Guid[] Ids, string QueryUrl, string SearchUrl, bool SearchReady, double IndexSeconds);

internal sealed record SharedWorld(int Folders, string PageUrl)
{
    public const string MemberName = "member";
    public const string MemberPassword = "perf-member-password-1";

    /// <summary>Task lists in the workspace, for the "mytasks" scenario.</summary>
    public const int TaskLists = 20;
}

internal sealed record StepReport(int Concurrency, int Requests, int Errors, double PerSecond, double P50Ms, double P95Ms, double P99Ms);

internal sealed record ScenarioReport(string Provider, string Scenario, double P95BudgetMs, int? LimitConcurrency, IReadOnlyList<StepReport> Steps);
