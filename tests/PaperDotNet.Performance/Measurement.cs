using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace PaperDotNet.Performance;

internal sealed record LatencyReport(int Samples, double? P50Ms, double? P95Ms, double? P99Ms);
internal sealed record StepReport(
    int Concurrency, int Attempted, int Requests, int Errors, int Cancelled, int TimedOut,
    double LaunchSeconds, double ElapsedSeconds, double PerSecond, LatencyReport SuccessLatency,
    LatencyReport AllAttemptsLatency, IReadOnlyDictionary<string, int> Outcomes,
    ResourceReport? ServerResources, ResourceReport GeneratorResources, bool WithinBudget)
{
    public int? FixtureItemsBefore { get; init; }
    public int? FixtureItemsAfter { get; init; }
}

internal sealed record ScenarioReport(string Provider, int Repeat, string Scenario, double P95BudgetMs,
    int? HighestPassingTestedConcurrency, bool ReachedConfiguredMaximum, IReadOnlyList<StepReport> Steps);

internal sealed class RequestFailure(HttpStatusCode status) : Exception($"HTTP {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
}

internal static class Measurement
{
    public static IEnumerable<int> ConcurrencyLadder(int max)
    {
        var current = 1;
        while (current < max)
        {
            yield return current;
            if (current > max / 2)
            {
                break;
            }

            current *= 2;
        }

        yield return max;
    }

    public static LatencyReport Latencies(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double? Percentile(double rank)
        {
            if (sorted.Length == 0)
            {
                return null;
            }

            var index = (sorted.Length - 1) * rank;
            var lower = (int)Math.Floor(index);
            var upper = (int)Math.Ceiling(index);
            return sorted[lower] + ((sorted[upper] - sorted[lower]) * (index - lower));
        }

        return new LatencyReport(sorted.Length, Percentile(0.5), Percentile(0.95), Percentile(0.99));
    }

    public static async Task<StepReport> RunAsync(
        Func<int, CancellationToken, Task> operation, int concurrency, double seconds, Options options,
        Process? server, CancellationToken cancellationToken)
    {
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var successes = new List<double>();
        var all = new List<double>();
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        var gate = new object();
        using var resources = new ResourceSampler(server);
        var watch = new Stopwatch();
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, concurrency).Select(async worker =>
        {
            await admission.Task;
            // Stop admission at the deadline; requests already admitted may drain.
            while (watch.Elapsed.TotalSeconds < seconds && !drain.IsCancellationRequested)
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(drain.Token);
                request.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                var started = Stopwatch.GetTimestamp();
                var outcome = "success";
                try
                {
                    await operation(worker, request.Token);
                }
                catch (OperationCanceledException) when (drain.IsCancellationRequested)
                {
                    outcome = "cancelled";
                }
                catch (OperationCanceledException)
                {
                    outcome = "timeout";
                }
                catch (RequestFailure ex)
                {
                    outcome = $"http_{((int)ex.Status).ToString(CultureInfo.InvariantCulture)}";
                }
                catch (HttpRequestException ex)
                {
                    outcome = ex.StatusCode is { } status ? $"http_{((int)status).ToString(CultureInfo.InvariantCulture)}" : "transport";
                }
                catch (Exception ex)
                {
                    outcome = ex.GetType().Name;
                }

                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (gate)
                {
                    all.Add(elapsed);
                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                    if (outcome == "success")
                    {
                        successes.Add(elapsed);
                    }
                }
            }
        }).ToArray();
        watch.Start();
        drain.CancelAfter(TimeSpan.FromSeconds(seconds + options.DrainSeconds));
        admission.SetResult();
        await Task.WhenAll(workers);
        watch.Stop();
        var snapshots = await resources.CompleteAsync();
        var success = Latencies(successes);
        var cancelled = outcomes.GetValueOrDefault("cancelled");
        var timedOut = outcomes.GetValueOrDefault("timeout");
        var errors = all.Count - successes.Count - cancelled - timedOut;
        return new StepReport(concurrency, all.Count, successes.Count, errors, cancelled, timedOut,
            seconds, watch.Elapsed.TotalSeconds, successes.Count / Math.Max(watch.Elapsed.TotalSeconds, 0.001),
            success, Latencies(all), outcomes, snapshots.Server, snapshots.Generator,
            successes.Count > 0 && errors == 0 && cancelled == 0 && timedOut == 0 && success.P95Ms <= options.P95Ms);
    }
}
