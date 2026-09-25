using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

public sealed record FileVersionResponse(
    int Number, bool IsCurrent, string FileName, string MediaType, long Size, string Sha256, string Source, DateTimeOffset CreatedAt, Guid? CreatedBy,
    ProcessingStatus ProcessingStatus, string? ProcessingError, Guid? OperationId, int? PageCount, string? TextLanguage, string? Languages)
{
    internal static FileVersionResponse From(FileVersion v) =>
        new(v.Number, v.IsCurrent, v.FileName, v.MediaType, v.Size, v.Sha256, v.Source, v.CreatedAt, v.CreatedBy,
            v.ProcessingStatus, v.ProcessingError, v.OperationId, v.PageCount, v.TextLanguage, v.Languages);
}

public sealed record FileVersionList([property: JsonPropertyName("value")] IReadOnlyList<FileVersionResponse> Value);

/// <summary>An existing document with the same content (DOC-10), among those the caller can read.</summary>
public sealed record DuplicateResponse(Guid WorkspaceId, Guid ListId, Guid ItemId, string? Title);

/// <summary>An uploaded document: the library item and its current file.</summary>
public sealed record DocumentResponse(
    Guid WorkspaceId, Guid ListId, Guid ItemId, uint Version, JsonObject Fields, FileVersionResponse File, IReadOnlyList<DuplicateResponse> Duplicates);

/// <summary>Library settings; <c>ocrLanguagesInherited</c> means the organization's default document languages apply.</summary>
public sealed record LibrarySettingsResponse(
    Guid ListId, DuplicatePolicy DuplicatePolicy, bool AutoProcess, OcrMode OcrMode, string OcrLanguages, bool OcrLanguagesInherited)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }


    internal static LibrarySettingsResponse From(Guid listId, LibrarySettings? s, string defaultLanguages) =>
        new(listId, s?.DuplicatePolicy ?? DuplicatePolicy.Warn, s?.AutoProcess ?? true, s?.OcrMode ?? OcrMode.Auto,
            s?.OcrLanguages ?? defaultLanguages, s?.OcrLanguages is null)
        { ETag = ETags.From(s?.Version ?? 0) };
}

/// <summary>Library settings; omitted values keep their current value. An empty <c>ocrLanguages</c> goes back to the organization's default.</summary>
public sealed record LibrarySettingsRequest(DuplicatePolicy? DuplicatePolicy, bool? AutoProcess, OcrMode? OcrMode, string? OcrLanguages);

/// <summary>On-demand processing: <c>forceOcr</c> runs OCR even when the PDF has text; <c>languages</c> like <c>deu+eng</c>.</summary>
public sealed record ProcessRequest(bool ForceOcr, string? Languages);

public sealed record ProcessResponse(Guid OperationId);

/// <summary>
/// Files of library items (DOC-01…03, DOC-10): upload (streamed into a temporary file, hashed and
/// type-checked by content), download with ranges, file versions and restore, and library settings.
/// Item access comes from the lists engine through <see cref="IListItemStore"/> (ADR-0011).
/// </summary>
internal static class DocumentEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // Requests may carry the largest allowed file plus the multipart overhead; larger ones are cut off early.
        var limit = endpoints.ServiceProvider.GetRequiredService<IOptions<DocumentsOptions>>().Value.MaxFileSize + (1024 * 1024);
        var lists = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}", "Documents");
        lists.MapPost("/documents", UploadAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("UploadDocument")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit);
        lists.MapGet("/documentSettings", GetSettingsAsync).RequireScope(DocumentScopes.Read).WithName("GetLibrarySettings");
        lists.MapPut("/documentSettings", UpdateSettingsAsync).RequireScope(DocumentScopes.Write).WithName("UpdateLibrarySettings");

        var file = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/file", "Documents");
        file.MapGet("", DownloadAsync).RequireScope(DocumentScopes.Read).WithName("DownloadFile").ProducesBinary();
        file.MapPut("", ReplaceAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("ReplaceFile")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit);
        file.MapGet("/versions", VersionsAsync).RequireScope(DocumentScopes.Read).WithName("ListFileVersions");
        file.MapGet("/versions/{number:int}", DownloadVersionAsync).RequireScope(DocumentScopes.Read).WithName("DownloadFileVersion").ProducesBinary();
        file.MapPost("/versions/{number:int}/restore", RestoreAsync).RequireScope(DocumentScopes.Write).WithName("RestoreFileVersion");
        file.MapPost("/process", ProcessAsync).RequireScope(DocumentScopes.Write).WithName("ProcessFile");
        file.MapGet("/pages/{page:int}/image", PageImageAsync).RequireScope(DocumentScopes.Read).WithName("GetPageImage").ProducesBinary("image/jpeg");
        file.MapGet("/thumbnail", ThumbnailAsync).RequireScope(DocumentScopes.Read).WithName("GetThumbnail").ProducesBinary("image/jpeg");

        endpoints.MapV1Group("me/inbox", "Documents")
            .MapPost("/documents", UploadToInboxAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("UploadToInbox")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit);
    }

    /// <summary>
    /// Multipart upload (<c>file</c>, optional <c>title</c>, <c>contentTypeId</c>, <c>folderId</c> and <c>languages</c> for
    /// OCR, e.g. <c>fra+eng</c>) that creates a library item.
    /// </summary>
    private static Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        Guid workspaceId, Guid listId, IFormFile? file, [FromForm] string? title, [FromForm] Guid? contentTypeId, [FromForm] string? languages,
        [FromForm] Guid? folderId, DocumentService documents, CancellationToken ct) =>
        documents.UploadAsync(workspaceId, listId, file, title, contentTypeId, languages, ct, folderId);

    /// <summary>Uploads into the caller's Inbox library (LST-07), created on first use.</summary>
    private static async Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadToInboxAsync(
        IFormFile? file, [FromForm] string? title, [FromForm] string? languages, IListItemStore items, DocumentService documents, CancellationToken ct)
    {
        var home = await items.EnsureHomeAsync(ct);
        return await documents.UploadAsync(home.WorkspaceId, home.InboxListId, file, title, null, languages, ct);
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> DownloadAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, DocumentsDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var version = item is null ? null : await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && v.IsCurrent, ct);
        return version is null ? ApiErrors.NotFound("The item has no file.") : await FileAsync(db, blobs, version, ct);
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> DownloadVersionAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, IListItemStore items, DocumentsDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var version = item is null ? null : await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && v.Number == number, ct);
        return version is null ? ApiErrors.NotFound() : await FileAsync(db, blobs, version, ct);
    }

    private static async Task<Results<Ok<FileVersionList>, ProblemHttpResult>> VersionsAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, DocumentsDbContext db, CancellationToken ct)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, ct) is null)
        {
            return ApiErrors.NotFound();
        }

        var versions = await db.FileVersions.AsNoTracking().Where(v => v.ItemId == itemId).OrderByDescending(v => v.Number).ToListAsync(ct);
        return TypedResults.Ok(new FileVersionList(versions.Select(FileVersionResponse.From).ToList()));
    }

    /// <summary>A new file version for an existing item; <c>If-Match</c> with the file's ETag is checked when sent.</summary>
    private static Task<Results<Ok<DocumentResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid workspaceId, Guid listId, Guid itemId, IFormFile? file, [FromForm] string? languages, HttpRequest http, DocumentService documents,
        CancellationToken ct) =>
        documents.ReplaceAsync(workspaceId, listId, itemId, file, languages, http.Headers.IfMatch.ToString(), ct);

    /// <summary>
    /// Processes the current file again (DOC-07): text extraction, OCR (forced or automatic) and
    /// thumbnails. Answers 202 with the operation; progress also arrives as live events (API-07).
    /// </summary>
    private static async Task<Results<Accepted<ProcessResponse>, ValidationProblem, ProblemHttpResult>> ProcessAsync(
        Guid workspaceId, Guid listId, Guid itemId, ProcessRequest? request, IListItemStore items, DocumentsDbContext db,
        ProcessingScheduler scheduler, CancellationToken ct)
    {
        if (request?.Languages is { } languages && !ProcessingScheduler.IsValidLanguageList(languages))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["languages"] = ["Tesseract language codes joined with '+', e.g. 'deu+eng'."] });
        }

        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var version = item is null ? null : await db.FileVersions.FirstOrDefaultAsync(v => v.ItemId == itemId && v.IsCurrent, ct);
        if (version is null)
        {
            return ApiErrors.NotFound("The item has no file.");
        }

        if (item!.Access < WorkspaceAccessLevel.Contribute)
        {
            return DocumentService.Forbidden();
        }

        if (request?.Languages is { } chosen)
        {
            version.Languages = chosen; // DOC-17: kept for this file and its next versions.
        }

        var operationId = await scheduler.ScheduleAsync(version, request?.ForceOcr ?? false, request?.Languages, ct);
        return TypedResults.Accepted($"{ApiRoutes.V1}/operations/{operationId}", new ProcessResponse(operationId));
    }

    /// <summary>
    /// A page as JPEG (DOC-04), <c>width</c> rounded up to 200, 800 or 1600 pixels; <c>version</c>
    /// selects an older file version. TIFF files can be shown once OCR produced their PDF.
    /// </summary>
    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> PageImageAsync(
        Guid workspaceId, Guid listId, Guid itemId, int page, int? width, int? version, IListItemStore items, DocumentsDbContext db,
        PageRenderer renderer, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var fileVersion = item is null
            ? null
            : await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && (version == null ? v.IsCurrent : v.Number == version), ct);
        if (fileVersion is null)
        {
            return ApiErrors.NotFound("The item has no file.");
        }

        var size = PageRenderer.WidthFor(width);
        var image = await renderer.RenderAsync(fileVersion, page, size, ct);
        return image is null
            ? ApiErrors.NotFound("The page cannot be shown (missing page, or a TIFF that is not processed yet).")
            : TypedResults.File(image, "image/jpeg", entityTag: new EntityTagHeaderValue($"\"{fileVersion.Sha256}-{page}-{size}\""));
    }

    private static Task<Results<FileStreamHttpResult, ProblemHttpResult>> ThumbnailAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, DocumentsDbContext db, PageRenderer renderer, CancellationToken ct) =>
        PageImageAsync(workspaceId, listId, itemId, 1, PageRenderer.Widths[0], null, items, db, renderer, ct);

    /// <summary>Makes an earlier version current again, as a new version (the history is kept).</summary>
    private static Task<Results<Ok<FileVersionResponse>, ProblemHttpResult>> RestoreAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, DocumentService documents, CancellationToken ct) =>
        documents.RestoreAsync(workspaceId, listId, itemId, number, ct);

    private static async Task<Results<Ok<LibrarySettingsResponse>, ProblemHttpResult>> GetSettingsAsync(
        Guid workspaceId, Guid listId, IListItemStore items, DocumentsDbContext db, IUserPreferences preferences, HttpResponse response, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        if (list is not { IsLibrary: true })
        {
            return ApiErrors.NotFound("The library was not found.");
        }

        var settings = await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(s => s.ListId == listId, ct);
        ETags.Set(response, settings?.Version ?? 0);
        return TypedResults.Ok(LibrarySettingsResponse.From(listId, settings, (await preferences.GetDefaultsAsync(ct)).DocumentLanguages));
    }

    /// <summary>Changes the library's document settings (needs Manage on the library).</summary>
    private static async Task<Results<Ok<LibrarySettingsResponse>, ValidationProblem, ProblemHttpResult>> UpdateSettingsAsync(
        Guid workspaceId, Guid listId, LibrarySettingsRequest request, IListItemStore items, DocumentsDbContext db,
        IUserPreferences preferences, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        if (request.OcrLanguages is { Length: > 0 } languages && !ProcessingScheduler.IsValidLanguageList(languages))
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["ocrLanguages"] = ["Tesseract language codes joined with '+', e.g. 'deu+eng'."] });
        }

        var list = await items.GetListAsync(workspaceId, listId, ct);
        if (list is not { IsLibrary: true })
        {
            return ApiErrors.NotFound("The library was not found.");
        }

        if (list.Access < WorkspaceAccessLevel.Manage)
        {
            return DocumentService.Forbidden();
        }

        var settings = await db.LibrarySettings.FirstOrDefaultAsync(s => s.ListId == listId, ct);
        if (ETags.TryGetIfMatch(http, out var expected) && expected != (settings?.Version ?? 0))
        {
            return ApiErrors.PreconditionFailed();
        }

        if (settings is null)
        {
            settings = new LibrarySettings { Id = Ids.New(), ListId = listId };
            db.LibrarySettings.Add(settings);
        }

        settings.DuplicatePolicy = request.DuplicatePolicy ?? settings.DuplicatePolicy;
        settings.AutoProcess = request.AutoProcess ?? settings.AutoProcess;
        settings.OcrMode = request.OcrMode ?? settings.OcrMode;
        settings.OcrLanguages = request.OcrLanguages is null ? settings.OcrLanguages : request.OcrLanguages.Length == 0 ? null : request.OcrLanguages;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, settings.Version);
        return TypedResults.Ok(LibrarySettingsResponse.From(listId, settings, (await preferences.GetDefaultsAsync(ct)).DocumentLanguages));
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> FileAsync(
        DocumentsDbContext db, IBlobStore blobs, FileVersion version, CancellationToken ct)
    {
        var stored = await db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == version.StoredFileId, ct);
        var stream = await blobs.OpenReadAsync(stored.BlobKey, ct);
        if (stream is null)
        {
            return ApiErrors.Problem(StatusCodes.Status500InternalServerError, "fileContentMissing", "The stored content of the file is missing.");
        }

        return TypedResults.File(stream, version.MediaType, version.FileName, version.CreatedAt,
            new EntityTagHeaderValue(DocumentService.ETag(version)), enableRangeProcessing: true);
    }
}

public sealed class DocumentsOptions
{
    public const string Section = "Documents";

    /// <summary>Largest accepted file in bytes (default 100 MB).</summary>
    public long MaxFileSize { get; set; } = 100L * 1024 * 1024;

    /// <summary>The Tesseract executable (in the container image; <c>tesseract</c> on the path).</summary>
    public string TesseractPath { get; set; } = "tesseract";

    /// <summary>Resolution of page images for OCR.</summary>
    public int OcrDpi { get; set; } = 300;

    /// <summary>Pages of a PDF that are OCRed at most.</summary>
    public int MaxOcrPages { get; set; } = 500;

    public TimeSpan OcrTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Upload, replace and restore: spool, check, store once, then create the item and the version.</summary>
internal sealed class DocumentService(
    IListItemStore items, DocumentsDbContext db, FileIntake intake, ProcessingScheduler processing, IOptions<DocumentsOptions> options,
    AuditOverrides stamps)
{
    private const int MaxDuplicates = 20;

    public static string ETag(FileVersion version) => $"\"{version.Sha256}\"";

    public static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    public async Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        Guid workspaceId, Guid listId, IFormFile? file, string? title, Guid? contentTypeId, string? languages, CancellationToken ct,
        Guid? folderId = null)
    {
        if (file is null)
        {
            return MissingFile();
        }

        if (InvalidLanguages(languages) is { } invalidLanguages)
        {
            return invalidLanguages;
        }

        var list = await items.GetListAsync(workspaceId, listId, ct);
        if (list is null)
        {
            return ApiErrors.NotFound();
        }

        if (!list.IsLibrary)
        {
            return ApiErrors.Problem(StatusCodes.Status400BadRequest, "notALibrary", "Files can only be uploaded into libraries.");
        }

        if (list.Access < WorkspaceAccessLevel.Contribute)
        {
            return Forbidden();
        }

        if (folderId is { } folder && await items.GetAsync(workspaceId, listId, folder, ct) is not { IsFolder: true })
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["folderId"] = ["No folder with this id in the library."] });
        }

        await using var spooled = await SpoolAsync(file, ct);
        if (Check(spooled) is { } invalid)
        {
            return invalid;
        }

        var duplicates = await DuplicatesAsync(spooled.Sha256, exceptItem: null, ct);
        var policy = await PolicyAsync(listId, ct);
        if (policy == DuplicatePolicy.Block && duplicates.Count > 0)
        {
            return DuplicateBlocked(duplicates);
        }

        var stored = await intake.StoreAsync(spooled, ct);
        var fileName = FileName(file.FileName, spooled.MediaType!);
        var fields = new JsonObject { ["title"] = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title };
        var created = await items.CreateAsync(workspaceId, listId, fields, contentTypeId, folderId, ct);
        if (!created.Succeeded)
        {
            return ItemProblem(created);
        }

        var item = created.Item!;
        var version = NewVersion(item, stored, fileName, number: 1, "upload", Languages(languages));
        db.FileVersions.Add(version);
        await db.SaveChangesAsync(ct);
        await processing.ScheduleIfAutomaticAsync(version, ct);
        return TypedResults.Created(
            $"{ApiRoutes.V1}/workspaces/{workspaceId}/lists/{listId}/items/{item.Id}/file",
            Response(item, version, policy == DuplicatePolicy.Warn ? duplicates : []));
    }

    public async Task<Results<Ok<DocumentResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid workspaceId, Guid listId, Guid itemId, IFormFile? file, string? languages, string? ifMatch, CancellationToken ct)
    {
        if (file is null)
        {
            return MissingFile();
        }

        if (InvalidLanguages(languages) is { } invalidLanguages)
        {
            return invalidLanguages;
        }

        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (item.Access < WorkspaceAccessLevel.Contribute)
        {
            return Forbidden();
        }

        var current = await db.FileVersions.FirstOrDefaultAsync(v => v.ItemId == itemId && v.IsCurrent, ct);
        if (!string.IsNullOrEmpty(ifMatch) && (current is null || ifMatch != ETag(current)))
        {
            return ApiErrors.PreconditionFailed();
        }

        await using var spooled = await SpoolAsync(file, ct);
        if (Check(spooled) is { } invalid)
        {
            return invalid;
        }

        var duplicates = await DuplicatesAsync(spooled.Sha256, exceptItem: itemId, ct);
        var policy = await PolicyAsync(listId, ct);
        if (policy == DuplicatePolicy.Block && duplicates.Count > 0)
        {
            return DuplicateBlocked(duplicates);
        }

        var stored = await intake.StoreAsync(spooled, ct);
        var version = await AddVersionAsync(item, current, stored, FileName(file.FileName, spooled.MediaType!), "upload", Languages(languages), ct);
        return version is null
            ? ApiErrors.Conflict("concurrentChange", "The file was changed at the same time; try again.")
            : TypedResults.Ok(Response(item, version, policy == DuplicatePolicy.Warn ? duplicates : []));
    }

    public async Task<Results<Ok<FileVersionResponse>, ProblemHttpResult>> RestoreAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var source = item is null ? null : await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && v.Number == number, ct);
        if (source is null)
        {
            return ApiErrors.NotFound();
        }

        if (item!.Access < WorkspaceAccessLevel.Contribute)
        {
            return Forbidden();
        }

        var current = await db.FileVersions.FirstOrDefaultAsync(v => v.ItemId == itemId && v.IsCurrent, ct);
        var stored = await db.StoredFiles.FirstAsync(f => f.Id == source.StoredFileId, ct);
        var version = await AddVersionAsync(item, current, stored, source.FileName, "restore", null, ct);
        return version is null
            ? ApiErrors.Conflict("concurrentChange", "The file was changed at the same time; try again.")
            : TypedResults.Ok(FileVersionResponse.From(version));
    }

    /// <summary>
    /// Gives an item its first file (templates and imports, PRV-04): checked like an upload (size, type) and processed
    /// like one. Nothing happens when the item already has a file. Returns an error message, or null.
    /// </summary>
    public async Task<string?> AttachAsync(ListItemData item, Stream content, string fileName, CancellationToken ct)
    {
        if (await db.FileVersions.AnyAsync(v => v.ItemId == item.Id, ct))
        {
            return null;
        }

        await using var spooled = await FileIntake.SpoolAsync(content, options.Value.MaxFileSize, ct);
        if (spooled.TooLarge)
        {
            return $"the file has more than {options.Value.MaxFileSize} bytes";
        }

        if (spooled.MediaType is null)
        {
            return "only PDF, TIFF, JPEG and PNG files are supported";
        }

        var stored = await intake.StoreAsync(spooled, ct);
        var version = await AddVersionAsync(item, null, stored, FileName(fileName, spooled.MediaType), "import", null, ct);
        return version is null ? "the item's file was changed at the same time" : null;
    }

    /// <summary>
    /// Adds an imported version to an item (packages, PLT-13/15): checked like an upload, with its original source, languages
    /// and stamps. With page texts it counts as processed; otherwise only the last (current) version is processed.
    /// Returns an error message, or null.
    /// </summary>
    public async Task<string?> ImportVersionAsync(
        ListItemData item, Stream content, string fileName, ImportedVersion imported, bool isCurrent, CancellationToken ct)
    {
        await using var spooled = await FileIntake.SpoolAsync(content, options.Value.MaxFileSize, ct);
        if (spooled.TooLarge)
        {
            return $"the file has more than {options.Value.MaxFileSize} bytes";
        }

        if (spooled.MediaType is null)
        {
            return "only PDF, TIFF, JPEG and PNG files are supported";
        }

        var stored = await intake.StoreAsync(spooled, ct);
        var current = await db.FileVersions.FirstOrDefaultAsync(v => v.ItemId == item.Id && v.IsCurrent, ct);
        var hasText = imported.Pages is { Count: > 0 };
        var version = await AddVersionAsync(
            item, current, stored, FileName(fileName, spooled.MediaType), imported.Source ?? "import", imported.Languages, ct,
            process: isCurrent && !hasText && !imported.Processed, stamp: imported.Stamp);
        if (version is null)
        {
            return "the item's file was changed at the same time";
        }

        if (!hasText && imported.Processed)
        {
            // Processed at the source without text (e.g. the original of an OCR version).
            version.TextLanguage = imported.TextLanguage;
            version.ProcessingStatus = ProcessingStatus.Succeeded;
            await db.SaveChangesAsync(ct);
        }

        if (hasText)
        {
            await SavePagesAsync(stored.Id, imported.Pages!, ct);
            version.PageCount = imported.Pages!.Count;
            version.TextLanguage = imported.TextLanguage;
            version.ProcessingStatus = ProcessingStatus.Succeeded;
            await db.SaveChangesAsync(ct);
            if (isCurrent)
            {
                await items.ReindexAsync(item.Id, ct);
            }
        }

        return null;
    }

    /// <summary>
    /// Adds the next version and makes it current; null when another change won the race. The new version keeps
    /// the current one's languages unless <paramref name="languages"/> are given.
    /// </summary>
    private async Task<FileVersion?> AddVersionAsync(
        ListItemData item, FileVersion? current, StoredFile stored, string fileName, string source, string? languages, CancellationToken ct,
        bool process = true, AuditStamp? stamp = null)
    {
        var number = (await db.FileVersions.Where(v => v.ItemId == item.Id).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
        current?.IsCurrent = false;
        var version = NewVersion(item, stored, fileName, number, source, languages ?? current?.Languages);
        if (stamp is not null)
        {
            stamps.Set(version.Id, stamp);
        }

        db.FileVersions.Add(version);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return null;
        }

        if (process)
        {
            await processing.ScheduleIfAutomaticAsync(version, ct);
        }

        return version;
    }

    /// <summary>
    /// A new current version made from the current one (page operations, DOC-05/06), with the given page texts.
    /// It is complete already, so it is not processed again. Null when another change won the race.
    /// </summary>
    internal async Task<FileVersion?> AddDerivedVersionAsync(
        ListItemData item, FileVersion current, StoredFile stored, IReadOnlyList<string> pageTexts, CancellationToken ct)
    {
        await SavePagesAsync(stored.Id, pageTexts, ct);
        var version = await AddVersionAsync(item, current, stored, Path.ChangeExtension(current.FileName, ".pdf"), "pages", null, ct, process: false);
        if (version is not null)
        {
            Complete(version, current, pageTexts.Count);
            await db.SaveChangesAsync(ct);
            await FinishAsync(item.Id, version, ct);
        }

        return version;
    }

    /// <summary>
    /// A new document in a library from pages of another file (extract, DOC-06): created like an upload, in
    /// <paramref name="folderId"/> when given, with the given page texts and without processing it again.
    /// </summary>
    internal async Task<(ListItemData? Item, FileVersion? Version, ListItemResult? Problem)> CreateDerivedAsync(
        Guid workspaceId, Guid listId, Guid? folderId, string title, StoredFile stored, FileVersion from, IReadOnlyList<string> pageTexts,
        CancellationToken ct)
    {
        var created = await items.CreateAsync(workspaceId, listId, new JsonObject { ["title"] = title }, null, folderId, ct);
        if (!created.Succeeded)
        {
            return (null, null, created);
        }

        var item = created.Item!;
        await SavePagesAsync(stored.Id, pageTexts, ct);
        var version = NewVersion(item, stored, Path.ChangeExtension(from.FileName, ".pdf"), number: 1, "pages", from.Languages);
        Complete(version, from, pageTexts.Count);
        db.FileVersions.Add(version);
        await db.SaveChangesAsync(ct);
        await FinishAsync(item.Id, version, ct);
        return (item, version, null);
    }

    /// <summary>Indexes the item; a version whose source was never processed is processed like an upload.</summary>
    private async Task FinishAsync(Guid itemId, FileVersion version, CancellationToken ct)
    {
        if (version.ProcessingStatus == ProcessingStatus.Succeeded)
        {
            await items.ReindexAsync(itemId, ct);
        }
        else
        {
            await processing.ScheduleIfAutomaticAsync(version, ct);
        }
    }

    private static void Complete(FileVersion version, FileVersion from, int pageCount)
    {
        version.PageCount = pageCount;
        version.TextLanguage = from.TextLanguage;
        version.ProcessingStatus = from.ProcessingStatus == ProcessingStatus.Succeeded ? ProcessingStatus.Succeeded : ProcessingStatus.None;
    }

    private async Task SavePagesAsync(Guid storedFileId, IReadOnlyList<string> pageTexts, CancellationToken ct)
    {
        if (pageTexts.All(string.IsNullOrEmpty) || await db.Pages.AnyAsync(p => p.StoredFileId == storedFileId, ct))
        {
            return;
        }

        db.Pages.AddRange(pageTexts.Select((text, index) => new StoredFilePage { StoredFileId = storedFileId, PageNumber = index + 1, Text = text }));
        await db.SaveChangesAsync(ct);
    }

    private async Task<SpooledFile> SpoolAsync(IFormFile file, CancellationToken ct)
    {
        await using var content = file.OpenReadStream();
        return await FileIntake.SpoolAsync(content, options.Value.MaxFileSize, ct);
    }

    private ProblemHttpResult? Check(SpooledFile spooled) =>
        spooled.TooLarge
            ? ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "fileTooLarge", $"Files may have at most {options.Value.MaxFileSize} bytes.")
            : spooled.MediaType is null
                ? ApiErrors.Problem(StatusCodes.Status415UnsupportedMediaType, "unsupportedFileType", "Only PDF, TIFF, JPEG and PNG files are supported.")
                : null;

    /// <summary>Current files with the same content that the caller can read.</summary>
    private async Task<List<DuplicateResponse>> DuplicatesAsync(string sha256, Guid? exceptItem, CancellationToken ct)
    {
        var candidates = await db.FileVersions.AsNoTracking()
            .Where(v => v.Sha256 == sha256 && v.IsCurrent && v.ItemId != exceptItem)
            .OrderBy(v => v.CreatedAt)
            .Take(MaxDuplicates)
            .Select(v => new { v.WorkspaceId, v.ListId, v.ItemId })
            .ToListAsync(ct);
        var result = new List<DuplicateResponse>();
        foreach (var candidate in candidates)
        {
            if (await items.GetAsync(candidate.WorkspaceId, candidate.ListId, candidate.ItemId, ct) is { } visible)
            {
                result.Add(new DuplicateResponse(candidate.WorkspaceId, candidate.ListId, candidate.ItemId, visible.Fields["title"]?.GetValue<string>()));
            }
        }

        return result;
    }

    private async Task<DuplicatePolicy> PolicyAsync(Guid listId, CancellationToken ct) =>
        await db.LibrarySettings.AsNoTracking().Where(s => s.ListId == listId).Select(s => (DuplicatePolicy?)s.DuplicatePolicy).FirstOrDefaultAsync(ct)
        ?? DuplicatePolicy.Warn;

    private static string? Languages(string? languages) => string.IsNullOrWhiteSpace(languages) ? null : languages.Trim();

    private static ValidationProblem? InvalidLanguages(string? languages) =>
        Languages(languages) is { } value && !ProcessingScheduler.IsValidLanguageList(value)
            ? ApiErrors.Validation(new Dictionary<string, string[]> { ["languages"] = ["Tesseract language codes joined with '+', e.g. 'deu+eng'."] })
            : null;

    private static FileVersion NewVersion(ListItemData item, StoredFile stored, string fileName, int number, string source, string? languages) => new()
    {
        Languages = languages,
        Id = Ids.New(),
        WorkspaceId = item.WorkspaceId,
        ListId = item.ListId,
        ItemId = item.Id,
        Number = number,
        IsCurrent = true,
        StoredFileId = stored.Id,
        Sha256 = stored.Sha256,
        Size = stored.Size,
        MediaType = stored.MediaType,
        FileName = fileName,
        Source = source,
    };

    /// <summary>The client's file name without path, with the extension of the detected type.</summary>
    private static string FileName(string? clientName, string mediaType)
    {
        // Browsers and tools may send full paths, with either separator.
        var baseName = (clientName ?? string.Empty).Split('/', '\\')[^1];
        var name = Path.GetFileNameWithoutExtension(baseName).Trim();
        if (name.Length == 0)
        {
            name = "document";
        }

        var extension = mediaType switch
        {
            FileTypes.Pdf => ".pdf",
            FileTypes.Tiff => ".tiff",
            FileTypes.Jpeg => ".jpg",
            _ => ".png",
        };
        return (name.Length > 200 ? name[..200] : name) + extension;
    }

    private static DocumentResponse Response(ListItemData item, FileVersion version, IReadOnlyList<DuplicateResponse> duplicates) =>
        new(item.WorkspaceId, item.ListId, item.Id, item.Version, item.Fields, FileVersionResponse.From(version), duplicates);

    private static ValidationProblem MissingFile() =>
        ApiErrors.Validation(new Dictionary<string, string[]> { ["file"] = ["A file is required (multipart/form-data, field 'file')."] });

    private static ProblemHttpResult DuplicateBlocked(IReadOnlyCollection<DuplicateResponse> duplicates) =>
        ApiErrors.Conflict("duplicateFile", $"The library does not accept duplicates; the same file exists as item {duplicates.First().ItemId}.");

    private static ProblemHttpResult ItemProblem(ListItemResult result) => result.Status switch
    {
        ListItemStatus.NotFound => ApiErrors.NotFound(),
        ListItemStatus.Forbidden => Forbidden(),
        ListItemStatus.Invalid => ApiErrors.Problem(StatusCodes.Status400BadRequest, "invalidFields",
            string.Join(" ", result.Errors!.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"))),
        _ => ApiErrors.Conflict("rejected", result.Message ?? "The change was rejected."),
    };
}
