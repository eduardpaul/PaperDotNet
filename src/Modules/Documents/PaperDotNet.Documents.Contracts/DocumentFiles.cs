using System.Text.Json.Nodes;

namespace PaperDotNet.Documents.Contracts;

public sealed record DocumentFile(Guid Id, Guid WorkspaceId, Guid ListId, Guid ItemId, int Number,
    string FileName, string MediaType, long Size, string Sha256, string Source, string? Languages)
{
    /// <summary>Effective OCR languages, including library and uploader preferences.</summary>
    public string? AnalysisLanguages { get; init; }
}

public sealed record DocumentCandidate(Guid Id, Guid RunId, DocumentFile Source, string State,
    string FileName, string MediaType, long Size, JsonObject Metrics);

/// <summary>File operations through the document storage and version pipeline. Caller access is checked by default.</summary>
public interface IDocumentFileStore
{
    IDocumentFileStore AsSystem();

    Task<DocumentFile?> GetCurrentAsync(Guid itemId, CancellationToken cancellationToken);

    Task<Stream?> OpenVersionAsync(Guid versionId, CancellationToken cancellationToken);

    Task<DocumentCandidate?> GetCandidateAsync(Guid id, CancellationToken cancellationToken);

    Task<DocumentCandidate?> FindSelectionAsync(Guid sourceVersionId, CancellationToken cancellationToken);

    /// <summary>Records an analysis that retained the immutable original, so later triggers reuse its file choice.</summary>
    Task<DocumentCandidate> RetainOriginalAsync(Guid id, Guid runId, Guid sourceVersionId, string reason,
        JsonObject metrics, CancellationToken cancellationToken);

    Task<DocumentCandidate> StageAsync(Guid id, Guid runId, Guid sourceVersionId, Stream content,
        string fileName, JsonObject metrics, CancellationToken cancellationToken);

    Task<Stream?> OpenCandidateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Promotes once, only while the reviewed source is current; releases its version reference atomically.</summary>
    Task<bool> PromoteAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> DiscardAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record DocumentOptimizationOptions(double TargetHeight = 12, double MinConfidence = 50,
    double Percentile = 5, int MaxAnalysisDimension = 2600, double MinimumScale = 0.05, int Quality = 80);

/// <summary>Owns the returned content stream; the caller disposes it after staging.</summary>
public sealed record DocumentOptimizationResult(Stream? Content, string MediaType, string Extension, JsonObject Metrics, string? SkipReason = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>Format adapters work on immutable input; they never change storage or approve a candidate.</summary>
public interface IDocumentOptimizationAdapter
{
    IReadOnlyList<string> MediaTypes { get; }

    Task<DocumentOptimizationResult> OptimizeAsync(Stream source, string? languages,
        DocumentOptimizationOptions options, CancellationToken cancellationToken);
}
