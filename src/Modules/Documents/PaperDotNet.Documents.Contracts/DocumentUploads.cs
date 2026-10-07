namespace PaperDotNet.Documents.Contracts;

public enum DocumentWriteStatus
{
    Ok,

    /// <summary>The library, folder or item does not exist or is not visible.</summary>
    NotFound,

    /// <summary>Visible, but the caller may not add or change files.</summary>
    Forbidden,

    /// <summary>Invalid input, e.g. not a library, or a new extension for a PDF or image.</summary>
    Invalid,

    /// <summary>Larger than <c>Documents:MaxFileSize</c>.</summary>
    TooLarge,

    /// <summary>The library blocks duplicates and the content exists already.</summary>
    DuplicateBlocked,

    /// <summary>The current file is not the expected one (someone else saved meanwhile).</summary>
    VersionMismatch,

    /// <summary>A concurrent change or a rule rejected the write.</summary>
    Conflict,
}

/// <summary>Outcome of an upload; <see cref="File"/> is the item's current file when it succeeded.</summary>
public sealed record DocumentWriteResult(DocumentWriteStatus Status, DocumentFile? File = null, string? Message = null)
{
    public bool Succeeded => Status == DocumentWriteStatus.Ok;
}

/// <summary>
/// Uploads into libraries from code (ADR-0047: WebDAV), through the same pipeline as the REST API: size limit, duplicate
/// policy, content type detection, versions and <c>document.added</c>. Acts as the current user with their permissions.
/// </summary>
public interface IDocumentUploads
{
    /// <summary>A new document in the library (or its folder), titled <paramref name="title"/> or by the file name.</summary>
    Task<DocumentWriteResult> UploadAsync(Guid workspaceId, Guid listId, Guid? folderId, Stream content, string fileName, string? title,
        CancellationToken cancellationToken);

    /// <summary>
    /// A new version of the item's file. <paramref name="expectedSha256"/> guards against lost updates. With
    /// <paramref name="replaceEmptyWithin"/>, an empty first version the caller created that recently is replaced
    /// instead (clients that create a file empty and write it right after, such as Windows Explorer).
    /// </summary>
    Task<DocumentWriteResult> ReplaceAsync(Guid workspaceId, Guid listId, Guid itemId, Stream content, string fileName, string? expectedSha256,
        TimeSpan? replaceEmptyWithin, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the current file (not a new version). The type of PDFs and images comes from their content, so their
    /// extension cannot change (<see cref="DocumentWriteStatus.Invalid"/>); other files take the type of the new name.
    /// </summary>
    Task<DocumentWriteResult> RenameAsync(Guid workspaceId, Guid listId, Guid itemId, string fileName, CancellationToken cancellationToken);
}
