using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PaperDotNet.Documents.Features;

/// <summary>A page of the result: its number in the current file, turned clockwise by <c>rotate</c> degrees (0, 90, 180, 270).</summary>
public sealed record PageSpec(int Page, int Rotate = 0);

/// <summary>The pages of the new version in order: pages left out are deleted (DOC-05).</summary>
public sealed record EditPagesRequest(IReadOnlyList<PageSpec>? Pages);

/// <summary>
/// Extracts pages into new documents (DOC-06): one document, or one per page with <c>separate</c>. The target library and
/// folder default to the source's library and folder; <c>remove</c> also deletes the pages from the source.
/// </summary>
public sealed record ExtractPagesRequest(
    IReadOnlyList<int>? Pages, bool Separate = false, bool Remove = false, Guid? WorkspaceId = null, Guid? ListId = null, Guid? FolderId = null,
    string? Title = null);

/// <summary>
/// Moves pages (all when omitted) into another document (DOC-06): <c>append</c> (default), <c>prepend</c>, or <c>replace</c>
/// its pages. A source left without pages goes to the recycle bin, so moving all pages merges two documents.
/// </summary>
public sealed record MovePagesRequest(Guid TargetWorkspaceId, Guid TargetListId, Guid TargetItemId, IReadOnlyList<int>? Pages = null, string? Position = null);

/// <summary>The result: the source's new version (null when it was deleted), new documents, the target's new version.</summary>
public sealed record PageOperationResponse(
    FileVersionResponse? Source, bool SourceDeleted, IReadOnlyList<DocumentResponse> Documents, FileVersionResponse? Target);

/// <summary>
/// Page operations on PDF files (DOC-05, DOC-06), all non-destructive: each change is a new file version and earlier
/// versions stay. Page texts are carried over, so nothing is OCRed again. Needs Contribute on every document involved.
/// </summary>
internal static class PageOperationEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var pages = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/file/pages", "Documents");
        pages.MapPut("", EditAsync).RequireScope(DocumentScopes.Write).WithName("EditPages");
        pages.MapPost("/extract", ExtractAsync).RequireScope(DocumentScopes.Write).WithName("ExtractPages");
        pages.MapPost("/move", MoveAsync).RequireScope(DocumentScopes.Write).WithName("MovePages");
    }

    private static async Task<Results<Ok<FileVersionResponse>, ValidationProblem, ProblemHttpResult>> EditAsync(
        Guid workspaceId, Guid listId, Guid itemId, EditPagesRequest request, HttpRequest http, PageEditor editor, CancellationToken ct)
    {
        var source = await editor.LoadAsync(workspaceId, listId, itemId, http.Headers.IfMatch.ToString(), ct);
        if (source.Problem is { } problem)
        {
            return problem;
        }

        var file = source.File!;
        var specs = request.Pages ?? [];
        var errors = new List<string>();
        if (specs.Count == 0)
        {
            errors.Add("At least one page is required; delete the document instead.");
        }

        if (specs.Any(p => p.Page < 1 || p.Page > file.PageCount))
        {
            errors.Add($"Pages are numbered from 1 to {file.PageCount}.");
        }

        if (specs.Select(p => p.Page).Distinct().Count() != specs.Count)
        {
            errors.Add("Each page may appear once.");
        }

        if (specs.Any(p => p.Rotate % 90 != 0))
        {
            errors.Add("Rotation is a multiple of 90 degrees.");
        }

        if (errors.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["pages"] = [.. errors] });
        }

        var version = await editor.SaveVersionAsync(file, [.. specs.Select(p => (file, p.Page, p.Rotate))], ct);
        return version is null ? Concurrent() : TypedResults.Ok(FileVersionResponse.From(version));
    }

    private static async Task<Results<Ok<PageOperationResponse>, ValidationProblem, ProblemHttpResult>> ExtractAsync(
        Guid workspaceId, Guid listId, Guid itemId, ExtractPagesRequest request, PageEditor editor, IListItemStore items, CancellationToken ct)
    {
        var source = await editor.LoadAsync(workspaceId, listId, itemId, null, ct);
        if (source.Problem is { } problem)
        {
            return problem;
        }

        var file = source.File!;
        var pages = request.Pages ?? [];
        if (PageErrors(pages, file.PageCount) is { } invalid)
        {
            return invalid;
        }

        if (request.Remove && pages.Count == file.PageCount)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["pages"] = ["Removing every page would leave an empty document; move the document instead."] });
        }

        var targetWorkspace = request.WorkspaceId ?? workspaceId;
        var targetList = request.ListId ?? listId;
        var folder = request.FolderId ?? (request.ListId is null ? file.Item.ParentId : null);
        var target = await items.GetListAsync(targetWorkspace, targetList, ct);
        if (target is null)
        {
            return ApiErrors.NotFound("The target library was not found.");
        }

        if (!target.IsLibrary)
        {
            return ApiErrors.Problem(StatusCodes.Status400BadRequest, "notALibrary", "Pages can only be extracted into libraries.");
        }

        if (target.Access < WorkspaceAccessLevel.Contribute)
        {
            return DocumentService.Forbidden();
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? file.Title : request.Title.Trim();
        var groups = request.Separate ? pages.Select(p => (IReadOnlyList<int>)[p]).ToList() : [pages];
        var documents = new List<DocumentResponse>();
        foreach (var group in groups)
        {
            var name = request.Separate ? $"{title} (page {group[0]})" : title;
            var created = await editor.CreateDocumentAsync(targetWorkspace, targetList, folder, name, file, group, ct);
            if (created.Problem is { } createProblem)
            {
                return createProblem;
            }

            documents.Add(created.Document!);
        }

        FileVersionResponse? sourceVersion = null;
        if (request.Remove)
        {
            var kept = Enumerable.Range(1, file.PageCount).Except(pages).Select(p => (file, p, 0)).ToList();
            var version = await editor.SaveVersionAsync(file, kept, ct);
            sourceVersion = version is null ? null : FileVersionResponse.From(version);
        }

        return TypedResults.Ok(new PageOperationResponse(sourceVersion, false, documents, null));
    }

    private static async Task<Results<Ok<PageOperationResponse>, ValidationProblem, ProblemHttpResult>> MoveAsync(
        Guid workspaceId, Guid listId, Guid itemId, MovePagesRequest request, PageEditor editor, CancellationToken ct)
    {
        var position = request.Position ?? "append";
        if (position is not ("append" or "prepend" or "replace"))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["position"] = ["append, prepend or replace is expected."] });
        }

        if (request.TargetItemId == itemId)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["targetItemId"] = ["Pages cannot be moved into the same document; edit its pages instead."] });
        }

        var source = await editor.LoadAsync(workspaceId, listId, itemId, null, ct);
        if (source.Problem is { } problem)
        {
            return problem;
        }

        var target = await editor.LoadAsync(request.TargetWorkspaceId, request.TargetListId, request.TargetItemId, null, ct);
        if (target.Problem is { } targetProblem)
        {
            return targetProblem;
        }

        var file = source.File!;
        var pages = request.Pages ?? [.. Enumerable.Range(1, file.PageCount)];
        if (PageErrors(pages, file.PageCount) is { } invalid)
        {
            return invalid;
        }

        var into = target.File!;
        var moved = pages.Select(p => (file, p, 0)).ToList();
        var existing = Enumerable.Range(1, into.PageCount).Select(p => (into, p, 0)).ToList();
        var result = position switch
        {
            "prepend" => [.. moved, .. existing],
            "replace" => moved,
            _ => existing.Concat(moved).ToList(),
        };
        var targetVersion = await editor.SaveVersionAsync(into, result, ct);
        if (targetVersion is null)
        {
            return Concurrent();
        }

        var kept = Enumerable.Range(1, file.PageCount).Except(pages).Select(p => (file, p, 0)).ToList();
        if (kept.Count == 0)
        {
            await editor.DeleteAsync(file, ct);
            return TypedResults.Ok(new PageOperationResponse(null, true, [], FileVersionResponse.From(targetVersion)));
        }

        var sourceVersion = await editor.SaveVersionAsync(file, kept, ct);
        return TypedResults.Ok(new PageOperationResponse(
            sourceVersion is null ? null : FileVersionResponse.From(sourceVersion), false, [], FileVersionResponse.From(targetVersion)));
    }

    private static ValidationProblem? PageErrors(IReadOnlyList<int> pages, int pageCount) =>
        pages.Count == 0 ? Invalid("At least one page is required.")
        : pages.Any(p => p < 1 || p > pageCount) ? Invalid($"Pages are numbered from 1 to {pageCount}.")
        : pages.Distinct().Count() != pages.Count ? Invalid("Each page may appear once.")
        : null;

    private static ValidationProblem Invalid(string message) => ApiErrors.Validation(new Dictionary<string, string[]> { ["pages"] = [message] });

    private static ProblemHttpResult Concurrent() => ApiErrors.Conflict("concurrentChange", "The file was changed at the same time; try again.");
}

/// <summary>A document's current PDF, ready for page operations.</summary>
internal sealed record EditableFile(ListItemData Item, FileVersion Version, StoredFile Stored, int PageCount, IReadOnlyDictionary<int, string> Texts)
{
    public string Title => Item.Fields["title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(Version.FileName);
}

/// <summary>Builds PDFs from pages of stored files (PDFsharp) and saves them as versions or new documents.</summary>
internal sealed class PageEditor(DocumentsDbContext db, IBlobStore blobs, IListItemStore items, FileIntake intake, DocumentService documents)
{
    public async Task<(EditableFile? File, ProblemHttpResult? Problem)> LoadAsync(
        Guid workspaceId, Guid listId, Guid itemId, string? ifMatch, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var version = item is null ? null : await db.FileVersions.FirstOrDefaultAsync(v => v.ItemId == itemId && v.IsCurrent, ct);
        if (version is null)
        {
            return (null, ApiErrors.NotFound("The item has no file."));
        }

        if (item!.Access < WorkspaceAccessLevel.Contribute)
        {
            return (null, DocumentService.Forbidden());
        }

        if (!string.IsNullOrEmpty(ifMatch) && ifMatch != DocumentService.ETag(version))
        {
            return (null, ApiErrors.PreconditionFailed());
        }

        if (version.MediaType != FileTypes.Pdf)
        {
            return (null, ApiErrors.Conflict("pdfRequired", "Pages can only be changed in PDF files; images get a PDF when they are processed."));
        }

        var stored = await db.StoredFiles.FirstAsync(f => f.Id == version.StoredFileId, ct);
        int pageCount;
        try
        {
            await using var content = await OpenAsync(stored, ct);
            using var pdf = PdfReader.Open(content, PdfDocumentOpenMode.Import);
            pageCount = pdf.PageCount;
        }
#pragma warning disable CA1031 // PDFsharp throws many exception types for damaged or encrypted files.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return (null, ApiErrors.Conflict("pdfNotEditable", "The PDF cannot be edited (it may be encrypted or damaged)."));
        }

        var texts = await db.Pages.AsNoTracking().Where(p => p.StoredFileId == stored.Id).ToDictionaryAsync(p => p.PageNumber, p => p.Text, ct);
        return (new EditableFile(item, version, stored, pageCount, texts), null);
    }

    /// <summary>A new current version of <paramref name="file"/> with the given pages; null when another change won.</summary>
    public async Task<FileVersion?> SaveVersionAsync(EditableFile file, IReadOnlyList<(EditableFile From, int Page, int Rotate)> pages, CancellationToken ct)
    {
        var stored = await BuildAsync(pages, ct);
        return await documents.AddDerivedVersionAsync(file.Item, file.Version, stored, Texts(pages), ct);
    }

    public async Task<(DocumentResponse? Document, ProblemHttpResult? Problem)> CreateDocumentAsync(
        Guid workspaceId, Guid listId, Guid? folderId, string title, EditableFile from, IReadOnlyList<int> pages, CancellationToken ct)
    {
        var spec = pages.Select(p => (from, p, 0)).ToList();
        var stored = await BuildAsync(spec, ct);
        var (item, version, problem) = await documents.CreateDerivedAsync(workspaceId, listId, folderId, title, stored, from.Version, Texts(spec), ct);
        return item is null
            ? (null, ApiErrors.Problem(StatusCodes.Status400BadRequest, "itemRejected",
                problem?.Message ?? string.Join(" ", problem?.Errors?.SelectMany(e => e.Value) ?? ["The document could not be created."])))
            : (new DocumentResponse(item.WorkspaceId, item.ListId, item.Id, item.Version, item.Fields, FileVersionResponse.From(version!), []), null);
    }

    public Task DeleteAsync(EditableFile file, CancellationToken ct) =>
        items.DeleteAsync(file.Item.WorkspaceId, file.Item.ListId, file.Item.Id, null, ct);

    private static List<string> Texts(IEnumerable<(EditableFile From, int Page, int Rotate)> pages) =>
        [.. pages.Select(p => p.From.Texts.GetValueOrDefault(p.Page) ?? string.Empty)];

    /// <summary>Copies the pages into a new PDF (rotations added to the page's own) and stores it.</summary>
    private async Task<StoredFile> BuildAsync(IReadOnlyList<(EditableFile From, int Page, int Rotate)> pages, CancellationToken ct)
    {
        var sources = new Dictionary<Guid, PdfDocument>();
        var streams = new List<Stream>();
        var output = Path.Combine(Path.GetTempPath(), $"pdn_pages_{Ids.New():N}.pdf");
        try
        {
            using (var result = new PdfDocument())
            {
                foreach (var (from, page, rotate) in pages)
                {
                    if (!sources.TryGetValue(from.Stored.Id, out var source))
                    {
                        var stream = await OpenAsync(from.Stored, ct);
                        streams.Add(stream);
                        source = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                        sources[from.Stored.Id] = source;
                    }

                    var added = result.AddPage(source.Pages[page - 1]);
                    if (rotate % 360 != 0)
                    {
                        added.Rotate = (((added.Rotate + rotate) % 360) + 360) % 360;
                    }
                }

                result.Save(output);
            }

            await using var content = File.OpenRead(output);
            await using var spooled = await FileIntake.SpoolAsync(content, long.MaxValue, ct);
            return await intake.StoreAsync(spooled, ct);
        }
        finally
        {
            foreach (var source in sources.Values)
            {
                source.Dispose();
            }

            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }

            File.Delete(output);
        }
    }

    /// <summary>The stored file as a seekable stream (PDFsharp needs one): a temporary copy that deletes itself.</summary>
    private async Task<Stream> OpenAsync(StoredFile stored, CancellationToken ct)
    {
        await using var blob = await blobs.OpenReadAsync(stored.BlobKey, ct)
            ?? throw new InvalidOperationException($"The stored file {stored.Sha256} is missing.");
        var copy = new FileStream(
            Path.Combine(Path.GetTempPath(), $"pdn_src_{Ids.New():N}.pdf"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await blob.CopyToAsync(copy, ct);
        copy.Position = 0;
        return copy;
    }
}
