using System.Globalization;

namespace PaperDotNet.Performance;

internal sealed record Options(
    IReadOnlyList<string> Providers, int Items, double Seconds, int MaxConcurrency, double P95Ms, string Output,
    bool SeparateProcess, bool Gate, int Repeats, double WarmupSeconds, double DrainSeconds,
    double RequestTimeoutSeconds, double ReadyTimeoutSeconds, string? HostPath)
{
    public double? MaxRssMiB { get; init; }
    public double IdleSeconds { get; init; }
    public static Options Parse(string[] args)
    {
        var known = new[] { "sqlite", "postgresql", "both", "--smoke", "--process", "--in-process", "--gate" };
        if (args.Any(a => !known.Contains(a, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Use sqlite|postgresql|both, --smoke, --process|--in-process, and --gate.");
        }

        if (args.Contains("--process") && args.Contains("--in-process"))
        {
            throw new ArgumentException("Choose one hosting mode.");
        }

        var smoke = args.Contains("--smoke");
        var providers = args.Where(a => a is "sqlite" or "postgresql" or "both").Distinct().ToArray();
        if (providers.Contains("both"))
        {
            providers = ["sqlite", "postgresql"];
        }

        return new Options(
            providers.Length == 0 ? ["sqlite"] : providers,
            smoke ? 20 : Integer("PERF_ITEMS", 200),
            smoke ? 1 : Number("PERF_SECONDS", 30),
            smoke ? 1 : Integer("PERF_MAX_CONCURRENCY", 16),
            Number("PERF_P95_MS", 1000),
            Environment.GetEnvironmentVariable("PERF_OUTPUT") ?? "perf-results.json",
            !args.Contains("--in-process"), args.Contains("--gate"),
            smoke ? 1 : Integer("PERF_REPEATS", 3),
            smoke ? 0.2 : Number("PERF_WARMUP_SECONDS", 5, allowZero: true),
            Number("PERF_DRAIN_SECONDS", 10), Number("PERF_REQUEST_TIMEOUT_SECONDS", 30),
            Number("PERF_READY_TIMEOUT_SECONDS", 120), Environment.GetEnvironmentVariable("PERF_HOST_PATH"))
        {
            MaxRssMiB = Environment.GetEnvironmentVariable("PERF_MAX_RSS_MIB") is null ? null : Number("PERF_MAX_RSS_MIB", 0),
            IdleSeconds = Number("PERF_IDLE_SECONDS", 0, allowZero: true),
        };
    }

    private static int Integer(string name, int fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        return text is null ? fallback : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new ArgumentException($"{name} must be a positive integer.");
    }

    private static double Number(string name, double fallback, bool allowZero = false)
    {
        var text = Environment.GetEnvironmentVariable(name);
        return text is null ? fallback : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && (allowZero ? value >= 0 : value > 0) && value <= 86400
            ? value : throw new ArgumentException($"{name} must be a finite {(allowZero ? "nonnegative" : "positive")} number <= 86400.");
    }
}
