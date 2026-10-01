using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// A provider's batch API in memory: answers <c>Batch: </c> and the input; can fail after submitting, or fail batches.
/// </summary>
internal sealed class FakeBatchClient : IAiBatchClient
{
    public List<(Guid BatchId, IReadOnlyList<AiBatchLine> Lines)> Submitted { get; } = [];

    public bool CrashAfterSubmit { get; set; }

    public AiBatchState State { get; set; } = AiBatchState.Completed;

    /// <summary>The answer to a line; default: <c>Batch: </c> and the input.</summary>
    public Func<AiBatchLine, string>? Answer { get; set; }

    private static string ProviderId(Guid batchId) => "batch_" + batchId.ToString("N");

    public Task<string> SubmitAsync(Guid batchId, IReadOnlyList<AiBatchLine> lines, CancellationToken cancellationToken)
    {
        lock (Submitted)
        {
            Submitted.Add((batchId, lines));
        }

        return CrashAfterSubmit ? throw new IOException("The connection was lost.") : Task.FromResult(ProviderId(batchId));
    }

    public Task<string?> FindAsync(Guid batchId, CancellationToken cancellationToken)
    {
        lock (Submitted)
        {
            return Task.FromResult(Submitted.Any(b => b.BatchId == batchId) ? ProviderId(batchId) : null);
        }
    }

    public Task<AiBatchStatus> GetAsync(string providerBatchId, CancellationToken cancellationToken)
    {
        IReadOnlyList<AiBatchLine> lines;
        lock (Submitted)
        {
            lines = Submitted.Last(b => ProviderId(b.BatchId) == providerBatchId).Lines;
        }

        return Task.FromResult(State == AiBatchState.Completed
            ? new AiBatchStatus(AiBatchState.Completed, [.. lines.Select(l => new AiBatchResult(l.CustomId, Answer?.Invoke(l) ?? "Batch: " + l.Input, 10, 5))])
            : new AiBatchStatus(State, [], State == AiBatchState.Failed ? "expired" : null));
    }
}
