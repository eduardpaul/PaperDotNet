using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>A version of a document's file. <c>pageCount</c> and <c>textLanguage</c> are set by the library's workflows (reading the text, OCR).</summary>
public sealed record FileVersionResponse(
    int Number, bool IsCurrent, string FileName, string MediaType, long Size, string Sha256, string Source, DateTimeOffset CreatedAt, Guid? CreatedBy,
    int? PageCount, string? TextLanguage, string? Languages)
{
    internal static FileVersionResponse From(FileVersion v) =>
        new(v.Number, v.IsCurrent, v.FileName, v.MediaType, v.Size, v.Sha256, v.Source, v.CreatedAt, v.CreatedBy, v.PageCount, v.TextLanguage, v.Languages);
}

public sealed record FileVersionList([property: JsonPropertyName("value")] IReadOnlyList<FileVersionResponse> Value);

/// <summary>An existing document with the same content (DOC-10), among those the caller can read.</summary>
public sealed record DuplicateResponse(Guid WorkspaceId, Guid ListId, Guid ItemId, string? Title);

/// <summary>An uploaded document: the library item and its current file.</summary>
public sealed record DocumentResponse(
    Guid WorkspaceId, Guid ListId, Guid ItemId, uint Version, JsonObject Fields, FileVersionResponse File, IReadOnlyList<DuplicateResponse> Duplicates);

/// <summary>
/// Library settings: <c>duplicatePolicy</c> (<c>allow</c>, <c>warn</c> or <c>block</c>); <c>ocrLanguagesInherited</c> means
/// the organization's default document languages apply.
/// </summary>
public sealed record LibrarySettingsResponse(Guid ListId, string DuplicatePolicy, string OcrLanguages, bool OcrLanguagesInherited)
{
    /// <summary>The ETag for <c>If-Match</c> on changes (the same as the <c>ETag</c> header).</summary>
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }

    internal static LibrarySettingsResponse From(Guid listId, LibrarySettings? s, string defaultLanguages) =>
        new(listId, s?.DuplicatePolicy ?? DuplicatePolicies.Warn, s?.OcrLanguages ?? defaultLanguages, s?.OcrLanguages is null)
        { ETag = ETags.From(s?.Version ?? 0) };
}

/// <summary>Library settings; omitted values keep their current value. An empty <c>ocrLanguages</c> goes back to the organization's default.</summary>
public sealed record LibrarySettingsRequest(string? DuplicatePolicy, string? OcrLanguages);

public sealed class DocumentsOptions
{
    public const string Section = "Documents";

    /// <summary>Largest accepted file in bytes (default 100 MB).</summary>
    public long MaxFileSize { get; set; } = 100L * 1024 * 1024;

    /// <summary>The Tesseract executable (in the container image; <c>tesseract</c> on the path).</summary>
    public string TesseractPath { get; set; } = "tesseract";

    /// <summary>
    /// <c>tesseract</c> (default) or <c>glm</c>. <c>glm</c> calls an Ollama server running the <c>glm-ocr</c> model
    /// (<see cref="GlmBaseUrl"/>).
    /// </summary>
    public string Engine { get; set; } = "tesseract";

    /// <summary>Ollama root address for <see cref="Engine"/> <c>glm</c> (no path; the client posts to <c>/api/generate</c>).</summary>
    public string GlmBaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>Ollama model name for <see cref="Engine"/> <c>glm</c>.</summary>
    public string GlmModel { get; set; } = "glm-ocr";

    /// <summary>
    /// Context length sent to GLM-OCR. A full-page photo uses several thousand tokens; too small a value truncates the
    /// image into garbage. 16384 fitted the receipt this was tried on and used about 5 GB.
    /// </summary>
    public int GlmContext { get; set; } = 16384;

    /// <summary>Maximum tokens GLM-OCR may write for one page.</summary>
    public int GlmMaxTokens { get; set; } = 8192;

    /// <summary>Resolution of page images for OCR.</summary>
    public int OcrDpi { get; set; } = 300;

    /// <summary>Pages of a PDF that are OCRed at most.</summary>
    public int MaxOcrPages { get; set; } = 500;

    /// <summary>Most pages "Render pages" (<c>document.renderPages</c>) renders per file.</summary>
    public int MaxRenderedPages { get; set; } = 500;

    public TimeSpan OcrTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Files of library items (DOC-01…03, DOC-10): upload (streamed into a temporary file, hashed and type-checked by
/// content), download with ranges, file versions and restore, and library settings. Item access comes from the lists
/// engine through <see cref="IListItemStore"/> (ADR-0011), acting as the caller.
/// </summary>
internal static class DocumentEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // Requests may carry the largest allowed file plus the multipart overhead; larger ones are cut off early.
        var limit = endpoints.ServiceProvider.GetRequiredService<IOptions<DocumentsOptions>>().Value.MaxFileSize + (1024 * 1024);
        var lists = endpoints.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}").WithTags("Documents");
        lists.MapPost("/documents", UploadAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("UploadDocument")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit)
            .WithDescription("Multipart upload (file, optional title, contentTypeId, folderId and languages for OCR, e.g. fra+eng) that creates a library item.");
        lists.MapGet("/documentSettings", GetSettingsAsync).RequireScope(DocumentScopes.Read).WithName("GetLibrarySettings");
        lists.MapPut("/documentSettings", UpdateSettingsAsync).RequireScope(DocumentScopes.Write).WithName("UpdateLibrarySettings");

        var file = endpoints.MapGroup("/v1.0/workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/file").WithTags("Documents");
        file.MapGet("", DownloadAsync).RequireScope(DocumentScopes.Read).WithName("DownloadFile");
        file.MapPut("", ReplaceAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("ReplaceFile")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit)
            .WithDescription("A new file version for an existing item; If-Match with the file's ETag is checked when sent.");
        file.MapGet("/versions", VersionsAsync).RequireScope(DocumentScopes.Read).WithName("ListFileVersions");
        file.MapGet("/versions/{number:int}", DownloadVersionAsync).RequireScope(DocumentScopes.Read).WithName("DownloadFileVersion");
        file.MapPost("/versions/{number:int}/restore", RestoreAsync).RequireScope(DocumentScopes.Write).WithName("RestoreFileVersion")
            .WithDescription("Makes an earlier version current again, as a new version (the history is kept).");
    }

    private static Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        Guid workspaceId, Guid listId, IFormFile? file, [FromForm] string? title, [FromForm] Guid? contentTypeId, [FromForm] string? languages,
        [FromForm] Guid? folderId, Caller caller, DocumentService documents, CancellationToken cancellationToken) =>
        documents.UploadAsync(caller.Actor, workspaceId, listId, file, title, contentTypeId, languages, folderId, cancellationToken);

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> DownloadAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, DocumentsDbContext db, IBlobStore blobs, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, cancellationToken);
        var version = item is null ? null : await DocumentQueries.CurrentAsync(db, caller.TenantId, itemId, cancellationToken);
        return version is null ? ApiErrors.NotFound("The item has no file.") : await FileAsync(db, blobs, version, cancellationToken);
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> DownloadVersionAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, Caller caller, IListItemStore items, DocumentsDbContext db, IBlobStore blobs,
        CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, cancellationToken);
        var version = item is null ? null : await DocumentQueries.VersionAsync(db, caller.TenantId, itemId, number, cancellationToken);
        return version is null ? ApiErrors.NotFound() : await FileAsync(db, blobs, version, cancellationToken);
    }

    private static async Task<Results<Ok<FileVersionList>, ProblemHttpResult>> VersionsAsync(
        Guid workspaceId, Guid listId, Guid itemId, Caller caller, IListItemStore items, DocumentsDbContext db, CancellationToken cancellationToken)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, cancellationToken) is null)
        {
            return ApiErrors.NotFound();
        }

        var versions = await DocumentQueries.VersionsAsync(db, caller.TenantId, itemId, cancellationToken);
        return TypedResults.Ok(new FileVersionList([.. versions.Select(FileVersionResponse.From)]));
    }

    private static Task<Results<Ok<DocumentResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid workspaceId, Guid listId, Guid itemId, IFormFile? file, [FromForm] string? languages, HttpRequest http, Caller caller, DocumentService documents,
        CancellationToken cancellationToken) =>
        documents.ReplaceAsync(caller.Actor, workspaceId, listId, itemId, file, languages, http.Headers.IfMatch.ToString(), cancellationToken);

    private static Task<Results<Ok<FileVersionResponse>, ProblemHttpResult>> RestoreAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, Caller caller, DocumentService documents, CancellationToken cancellationToken) =>
        documents.RestoreAsync(caller.Actor, workspaceId, listId, itemId, number, cancellationToken);

    private static async Task<Results<Ok<LibrarySettingsResponse>, ProblemHttpResult>> GetSettingsAsync(
        Guid workspaceId, Guid listId, Caller caller, IListItemStore items, DocumentsDbContext db, IUserPreferences preferences, HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (await items.GetListAsync(workspaceId, listId, cancellationToken) is not { IsLibrary: true })
        {
            return ApiErrors.NotFound("The library was not found.");
        }

        var settings = await DocumentQueries.SettingsAsync(db, caller.TenantId, listId, cancellationToken);
        ETags.Set(response, settings?.Version ?? 0);
        return TypedResults.Ok(LibrarySettingsResponse.From(listId, settings, (await preferences.GetDefaultsAsync(caller.TenantId, cancellationToken)).DocumentLanguages));
    }

    /// <summary>Changes the library's document settings (needs Manage on the library).</summary>
    private static async Task<Results<Ok<LibrarySettingsResponse>, ValidationProblem, ProblemHttpResult>> UpdateSettingsAsync(
        Guid workspaceId, Guid listId, LibrarySettingsRequest request, Caller caller, IListItemStore items, DocumentsDbContext db,
        IUserPreferences preferences, HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        if (request.OcrLanguages is { Length: > 0 } languages && !DocumentText.IsValidLanguageList(languages))
        {
            return ApiErrors.Validation("ocrLanguages", "Tesseract language codes joined with '+', e.g. 'deu+eng'.");
        }

        if (request.DuplicatePolicy is { } policy && !DuplicatePolicies.IsValid(policy))
        {
            return ApiErrors.Validation("duplicatePolicy", "allow, warn or block.");
        }

        var list = await items.GetListAsync(workspaceId, listId, cancellationToken);
        if (list is not { IsLibrary: true })
        {
            return ApiErrors.NotFound("The library was not found.");
        }

        if (list.Access < WorkspaceAccessLevel.Manage)
        {
            return DocumentService.Forbidden();
        }

        var settings = await DocumentQueries.SettingsAsync(db, caller.TenantId, listId, cancellationToken);
        if (ETags.TryGetIfMatch(http, out var expected) && expected != (settings?.Version ?? 0))
        {
            return ApiErrors.PreconditionFailed();
        }

        if (settings is null)
        {
            settings = new LibrarySettings { Id = Ids.New(), TenantId = caller.TenantId, ListId = listId };
            db.LibrarySettings.Add(settings);
        }

        settings.DuplicatePolicy = request.DuplicatePolicy ?? settings.DuplicatePolicy;
        settings.OcrLanguages = request.OcrLanguages is null ? settings.OcrLanguages : request.OcrLanguages.Length == 0 ? null : request.OcrLanguages;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, settings.Version);
        return TypedResults.Ok(LibrarySettingsResponse.From(listId, settings, (await preferences.GetDefaultsAsync(caller.TenantId, cancellationToken)).DocumentLanguages));
    }

    private static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> FileAsync(
        DocumentsDbContext db, IBlobStore blobs, FileVersion version, CancellationToken ct)
    {
        var stored = await DocumentQueries.StoredFileAsync(db, version.TenantId, version.StoredFileId, ct);
        if (stored is null || await blobs.OpenReadAsync(stored.BlobKey, ct) is not { } stream)
        {
            return ApiErrors.Problem(StatusCodes.Status500InternalServerError, "fileContentMissing", "The stored content of the file is missing.");
        }

        return TypedResults.File(stream, version.MediaType, version.FileName, version.CreatedAt,
            new EntityTagHeaderValue(DocumentService.ETag(version)), enableRangeProcessing: true);
    }
}
