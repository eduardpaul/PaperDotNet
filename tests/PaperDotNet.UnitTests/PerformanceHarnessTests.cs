using System.Net;
using PaperDotNet.Performance;
using PerformanceProgram = PaperDotNet.Performance.Program;

namespace PaperDotNet.UnitTests;

public sealed class PerformanceHarnessTests
{
    private static Options TestOptions => new(["sqlite"], 20, 0.1, 1, 1000, "unused.json", false, true, 1, 0, 1, 5, 5, null);

    [Fact]
    public void Ladder_includes_exact_cap_and_does_not_overflow()
    {
        Assert.Equal([1, 2, 4, 8, 12], Measurement.ConcurrencyLadder(12));
        Assert.Equal([1], Measurement.ConcurrencyLadder(1));
        var largest = Measurement.ConcurrencyLadder(int.MaxValue).ToArray();
        Assert.Equal(int.MaxValue, largest[^1]);
        Assert.Equal(32, largest.Length);
    }

    [Fact]
    public void No_samples_have_null_percentiles_instead_of_zero_latency()
    {
        Assert.Equal(new LatencyReport(0, null, null, null), Measurement.Latencies([]));
        var values = Measurement.Latencies([40, 10, 30, 20]);
        Assert.Equal(25, values.P50Ms);
        Assert.Equal(38.5, values.P95Ms);
    }

    [Fact]
    public async Task Admitted_requests_complete_after_launch_deadline_without_cancellation()
    {
        var result = await Measurement.RunAsync(async (_, ct) => await Task.Delay(250, ct), 2, 0.1, TestOptions, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Attempted);
        Assert.Equal(2, result.Requests);
        Assert.Equal(0, result.Cancelled);
        Assert.True(result.ElapsedSeconds >= 0.2);
        Assert.True(result.WithinBudget);
        Assert.Equal(2, result.SuccessLatency.Samples);
    }

    [Fact]
    public async Task Drain_expiry_is_recorded_and_fails_budget()
    {
        var options = TestOptions with { DrainSeconds = 0.1 };
        var result = await Measurement.RunAsync(async (_, ct) => await Task.Delay(5000, ct), 1, 0.1, options, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Cancelled);
        Assert.Equal(0, result.Requests);
        Assert.Equal(1, result.AllAttemptsLatency.Samples);
        Assert.False(result.WithinBudget);
    }

    [Fact]
    public async Task Request_timeout_is_separate_from_drain_cancellation()
    {
        var options = TestOptions with { RequestTimeoutSeconds = 0.1 };
        var result = await Measurement.RunAsync(async (_, ct) => await Task.Delay(5000, ct), 1, 0.05, options, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.TimedOut);
        Assert.Equal(0, result.Cancelled);
        Assert.False(result.WithinBudget);
    }

    [Fact]
    public async Task Http_and_transport_failures_preserve_attempt_counts_and_failure_latency()
    {
        var attempts = 0;
        var result = await Measurement.RunAsync(async (_, ct) =>
        {
            await Task.Delay(20, ct);
            switch (Interlocked.Increment(ref attempts))
            {
                case 1: throw new RequestFailure(HttpStatusCode.TooManyRequests);
                case 2: throw new HttpRequestException("connection failed");
            }
        }, 1, 0.15, TestOptions, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Outcomes["http_429"]);
        Assert.Equal(1, result.Outcomes["transport"]);
        Assert.Equal(2, result.Errors);
        Assert.Equal(result.Attempted, result.Requests + result.Errors + result.TimedOut + result.Cancelled);
        Assert.Equal(result.Attempted, result.AllAttemptsLatency.Samples);
        Assert.False(result.WithinBudget);
    }

    [Fact]
    public void Missing_provider_and_incomplete_scenarios_fail_even_without_gate()
    {
        var options = TestOptions with { Providers = ["sqlite", "postgresql"], Gate = false };
        var report = new RunReport(2, DateTimeOffset.UtcNow, options, new Dictionary<string, string>(), []);
        Assert.Equal(1, PerformanceProgram.ExitCode(report, options));
        report.Runs.Add(new ProviderRun("sqlite", 1) { Status = "completed" });
        report.Runs.Add(new ProviderRun("postgresql", 1) { Status = "failed" });
        Assert.Equal(1, PerformanceProgram.ExitCode(report, options));
    }

    [Fact]
    public async Task Gate_fails_latency_breach_and_exploratory_mode_can_report_it()
    {
        var options = TestOptions with { P95Ms = 1 };
        var step = await Measurement.RunAsync(async (_, ct) => await Task.Delay(30, ct), 1, 0.1, options, null, TestContext.Current.CancellationToken);
        var provider = new ProviderRun("sqlite", 1) { Status = "completed" };
        foreach (var scenario in new[] { "read", "query", "shared", "mytasks", "search", "create" })
        {
            provider.Scenarios.Add(new ScenarioReport("sqlite", 1, scenario, 1, null, false, [step]));
        }

        var report = new RunReport(2, DateTimeOffset.UtcNow, options, new Dictionary<string, string>(), [provider]);
        Assert.Equal(2, PerformanceProgram.ExitCode(report, options));
        Assert.Equal(0, PerformanceProgram.ExitCode(report, options with { Gate = false }));
    }

    [Fact]
    public void Options_reject_unknown_arguments_and_conflicting_hosts()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["sqltie"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--process", "--in-process"]));
    }

    [Fact]
    public async Task Memory_gate_requires_server_measurements_and_checks_phase_snapshots()
    {
        var step = await Measurement.RunAsync(async (_, ct) => await Task.Delay(20, ct), 1, 0.05,
            TestOptions, null, TestContext.Current.CancellationToken);
        var provider = new ProviderRun("sqlite", 1) { Status = "completed" };
        foreach (var name in new[] { "read", "query", "shared", "mytasks", "search", "create" })
        {
            provider.Scenarios.Add(new ScenarioReport("sqlite", 1, name, 1000, null, false, [step]));
        }
        var options = TestOptions with { MaxRssMiB = 256 };
        var report = new RunReport(2, DateTimeOffset.UtcNow, options, new Dictionary<string, string>(), [provider]);
        Assert.Equal(2, PerformanceProgram.ExitCode(report, options));
        Assert.Equal(0, PerformanceProgram.ExitCode(report, options with { Gate = false }));
        var server = new ResourceReport(123, 0, 0, 128 * 1024 * 1024, []);
        for (var i = 0; i < provider.Scenarios.Count; i++)
        {
            provider.Scenarios[i] = provider.Scenarios[i] with { Steps = [step with { ServerResources = server }] };
        }
        Assert.Equal(0, PerformanceProgram.ExitCode(report, options));
        provider.MemoryPhases.Add(new MemoryPhase("idle", 300 * 1024 * 1024));
        Assert.Equal(2, PerformanceProgram.ExitCode(report, options));
        provider.MemoryPhases.Clear();
        provider.Warmups.Add(new WarmupReport("read", step with { ServerResources = server with { PeakSampledWorkingSetBytes = 300 * 1024 * 1024 } }));
        Assert.Equal(2, PerformanceProgram.ExitCode(report, options));
        provider.Warmups.Clear();
        provider.IdleResources = server with { PeakSampledWorkingSetBytes = 300 * 1024 * 1024 };
        Assert.Equal(2, PerformanceProgram.ExitCode(report, options));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0")]
    public void Invalid_environment_values_fail_instead_of_selecting_defaults(string value)
    {
        var previous = Environment.GetEnvironmentVariable("PERF_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("PERF_SECONDS", value);
            Assert.Throws<ArgumentException>(() => Options.Parse(["sqlite", "--in-process"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERF_SECONDS", previous);
        }
    }
}
