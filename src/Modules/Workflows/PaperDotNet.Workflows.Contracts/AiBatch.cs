using System.Text.Json.Nodes;

namespace PaperDotNet.Workflows.Contracts;

/// <summary>
/// One question of an AI batch: <see cref="CustomId"/> identifies its answer (the same question is sent once).
/// <see cref="Images"/> are sent with the input for models that read images (e.g. the pages of a photographed receipt).
/// </summary>
public sealed record AiBatchLine(string CustomId, string Model, string Instructions, string Input, JsonObject? Schema, IReadOnlyList<AiBatchImage>? Images = null);

/// <summary>An image of an AI batch line (e.g. <c>image/jpeg</c>).</summary>
public sealed record AiBatchImage(string MediaType, byte[] Content);

/// <summary>The answer to one line (or its error), with the tokens it used.</summary>
public sealed record AiBatchResult(string CustomId, string? Text, long InputTokens = 0, long OutputTokens = 0, string? Error = null);

public enum AiBatchState
{
    /// <summary>Validating, running or finalizing.</summary>
    Running,

    /// <summary>Done; <see cref="AiBatchStatus.Results"/> holds the answers.</summary>
    Completed,

    /// <summary>Failed, expired or cancelled; lines without a result are queued again.</summary>
    Failed,
}

/// <summary>Where a provider's batch is: its state and, when finished, the results it has.</summary>
public sealed record AiBatchStatus(AiBatchState State, IReadOnlyList<AiBatchResult> Results, string? Error = null);

/// <summary>
/// A provider's batch API for AI activities with <c>execution: batch</c> (AI-08, ADR-0036), e.g. the Azure OpenAI or
/// OpenAI Batch API: cheaper, with a separate quota and results within a day. Register one and the <c>ai.batch</c>
/// activity (the "AI batch" workflow) sends the waiting questions in batches; without one, it answers them with the chat
/// model. A batch holds one model's lines.
/// </summary>
public interface IAiBatchClient
{
    /// <summary>Most lines in one batch.</summary>
    int MaxLines => 50_000;

    /// <summary>Submits the lines as one batch tagged with <paramref name="batchId"/>; returns the provider's batch id.</summary>
    Task<string> SubmitAsync(Guid batchId, IReadOnlyList<AiBatchLine> lines, CancellationToken cancellationToken);

    /// <summary>
    /// The provider's id of the batch tagged with <paramref name="batchId"/>, or null when there is none: when the step runs
    /// again after a crash or failure, it adopts a batch it already sent instead of paying for it twice.
    /// </summary>
    Task<string?> FindAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The batch's state, with the results once it has finished.</summary>
    Task<AiBatchStatus> GetAsync(string providerBatchId, CancellationToken cancellationToken);
}
