using System.Diagnostics;

namespace PaperDotNet.Performance;

internal sealed record ResourceSample(double Seconds, double CpuSeconds, long WorkingSetBytes);
internal sealed record ResourceReport(int ProcessId, double CpuSeconds, double AverageCores, long PeakSampledWorkingSetBytes,
    IReadOnlyList<ResourceSample> Samples);

/// <summary>OS process measurements. RSS includes native memory; it is not managed allocation or database memory.</summary>
internal sealed class ResourceSampler : IDisposable
{
    private readonly Process? server;
    private readonly Process generator = Process.GetCurrentProcess();
    private readonly CancellationTokenSource stop = new();
    private readonly List<ResourceSample> serverSamples = [];
    private readonly List<ResourceSample> generatorSamples = [];
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly Task sampling;

    public ResourceSampler(Process? server)
    {
        this.server = server;
        Sample();
        sampling = SampleAsync();
    }

    private void Sample()
    {
        Add(generator, generatorSamples);
        if (server is not null)
        {
            Add(server, serverSamples);
        }
    }

    private void Add(Process process, List<ResourceSample> samples)
    {
        process.Refresh();
        samples.Add(new ResourceSample(watch.Elapsed.TotalSeconds, process.TotalProcessorTime.TotalSeconds, process.WorkingSet64));
    }

    private async Task SampleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                Sample();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The final sample is taken after the sampling loop has stopped.
        }
    }

    public async Task<(ResourceReport? Server, ResourceReport Generator)> CompleteAsync()
    {
        await stop.CancelAsync();
        await sampling;
        Sample();
        static ResourceReport Report(int pid, List<ResourceSample> samples)
        {
            var cpu = samples[^1].CpuSeconds - samples[0].CpuSeconds;
            var elapsed = samples[^1].Seconds - samples[0].Seconds;
            return new ResourceReport(pid, cpu, cpu / Math.Max(elapsed, 0.001), samples.Max(s => s.WorkingSetBytes), samples);
        }

        return (server is null ? null : Report(server.Id, serverSamples), Report(generator.Id, generatorSamples));
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
        generator.Dispose();
    }
}
