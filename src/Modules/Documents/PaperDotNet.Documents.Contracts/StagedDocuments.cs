using System.Data.Common;
using System.Text.Json.Nodes;

namespace PaperDotNet.Documents.Contracts;

/// <summary>Durable temporary content, independent of workflows, selections and review policy.</summary>
public sealed record StagedDocument(Guid Id, string Owner, Guid CreatedBy, string FileName,
    string? Languages, string State, string? MediaType, long? Size, int? PageCount)
{
    public JsonObject Attributes { get; init; } = [];
}

public interface IStagedDocumentStore
{
    Task<StagedDocument?> GetAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>Snapshots optional domain attributes at creation. Retries must supply equivalent attributes.</summary>
    Task<StagedDocument> CreateAsync(Guid id, string owner, string fileName, string? languages, CancellationToken cancellationToken, JsonObject? attributes = null);
    /// <summary>Protects immutable input bytes from cleanup for the lifetime of this temporary document.</summary>
    Task RetainVersionsAsync(Guid id, IReadOnlyList<Guid> versionIds, CancellationToken cancellationToken);
    /// <summary>Stores immutable output. Null text leaves extraction pending; supplied PDF text includes successful blank pages.</summary>
    Task<StagedDocument> StageAsync(Guid id, Stream content, IReadOnlyList<string>? pageTexts, CancellationToken cancellationToken);
    Task<Stream?> OpenAsync(Guid id, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>An owner decides whether temporary content is still needed; no processing policy lives in Documents.</summary>
public interface IStagedDocumentRetention
{
    string Owner { get; }
    Task<bool> IsRetainedAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Conditional publishing participates in the caller's transaction and preserves existing file history.</summary>
public interface IDocumentPublisher
{
    Task LockVersionsAsync(IReadOnlyList<DocumentFile> versions, DbTransaction transaction, CancellationToken cancellationToken);
    Task PublishAsync(Guid stagedId, DocumentFile expected, string source, DbTransaction transaction, CancellationToken cancellationToken);
    Task AnnounceAsync(Guid stagedId, CancellationToken cancellationToken);
}
