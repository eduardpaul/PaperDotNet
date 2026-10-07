using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary><see cref="IDocumentUploads"/> on the same <see cref="DocumentService"/> as the REST API, so the rules cannot drift.</summary>
internal sealed class DocumentUploads(DocumentService documents, DocumentsDbContext db, IListItemStore items) : IDocumentUploads
{
    public async Task<DocumentWriteResult> UploadAsync(
        Guid workspaceId, Guid listId, Guid? folderId, Stream content, string fileName, string? title, CancellationToken cancellationToken)
    {
        var result = await documents.UploadAsync(workspaceId, listId, content, fileName, title, null, null, cancellationToken, folderId);
        return result.Result is Created<DocumentResponse> created
            ? await CurrentAsync(created.Value!.ItemId, cancellationToken)
            : Failure(result.Result);
    }

    public async Task<DocumentWriteResult> ReplaceAsync(
        Guid workspaceId, Guid listId, Guid itemId, Stream content, string fileName, string? expectedSha256, TimeSpan? replaceEmptyWithin,
        CancellationToken cancellationToken)
    {
        var ifMatch = expectedSha256 is null ? null : $"\"{expectedSha256}\"";
        var result = await documents.ReplaceAsync(workspaceId, listId, itemId, content, fileName, null, ifMatch, replaceEmptyWithin, cancellationToken);
        return result.Result is Ok<DocumentResponse> ? await CurrentAsync(itemId, cancellationToken) : Failure(result.Result);
    }

    public async Task<DocumentWriteResult> RenameAsync(Guid workspaceId, Guid listId, Guid itemId, string fileName, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, cancellationToken);
        var current = item is null ? null : await db.FileVersions.FirstOrDefaultAsync(v => v.ItemId == itemId && v.IsCurrent, cancellationToken);
        if (current is null)
        {
            return new(DocumentWriteStatus.NotFound);
        }

        if (item!.Access < WorkspaceAccessLevel.Contribute)
        {
            return new(DocumentWriteStatus.Forbidden);
        }

        var name = fileName.Split('/', '\\')[^1].Trim();
        if (name.Length == 0 || name.Length > 255)
        {
            return new(DocumentWriteStatus.Invalid, Message: "A file name has 1 to 255 characters.");
        }

        var stored = await db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == current.StoredFileId, cancellationToken);
        if (FileTypes.IsProcessable(stored.MediaType) && FileTypes.ForName(name) != stored.MediaType)
        {
            return new(DocumentWriteStatus.Invalid, Message: $"The type of this file comes from its content ({stored.MediaType}); its extension cannot change.");
        }

        current.FileName = name;
        current.MediaType = FileTypes.ForVersion(stored.MediaType, name);
        await db.SaveChangesAsync(cancellationToken);
        return new(DocumentWriteStatus.Ok, DocumentFileStore.Describe(current));
    }

    private async Task<DocumentWriteResult> CurrentAsync(Guid itemId, CancellationToken ct)
    {
        var version = await db.FileVersions.AsNoTracking().FirstAsync(v => v.ItemId == itemId && v.IsCurrent, ct);
        return new(DocumentWriteStatus.Ok, DocumentFileStore.Describe(version));
    }

    private static DocumentWriteResult Failure(IResult result) => result switch
    {
        ProblemHttpResult problem => new(Status(problem), Message: problem.ProblemDetails.Detail),
        ValidationProblem validation => new(DocumentWriteStatus.Invalid, Message: string.Join(" ", validation.ProblemDetails.Errors.SelectMany(e => e.Value))),
        _ => new(DocumentWriteStatus.Conflict),
    };

    private static DocumentWriteStatus Status(ProblemHttpResult problem) => problem.StatusCode switch
    {
        StatusCodes.Status404NotFound => DocumentWriteStatus.NotFound,
        StatusCodes.Status403Forbidden => DocumentWriteStatus.Forbidden,
        StatusCodes.Status413PayloadTooLarge => DocumentWriteStatus.TooLarge,
        StatusCodes.Status412PreconditionFailed => DocumentWriteStatus.VersionMismatch,
        StatusCodes.Status409Conflict when problem.ProblemDetails.Extensions.TryGetValue("code", out var code) && code is "duplicateFile" =>
            DocumentWriteStatus.DuplicateBlocked,
        StatusCodes.Status409Conflict => DocumentWriteStatus.Conflict,
        _ => DocumentWriteStatus.Invalid,
    };
}
