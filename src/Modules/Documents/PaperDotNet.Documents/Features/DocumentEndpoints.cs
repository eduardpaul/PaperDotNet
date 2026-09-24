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
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

public sealed record FileVersionResponse(
    int Number, bool IsCurrent, string FileName, string MediaType, long Size, string Sha256, string Source, DateTimeOffset CreatedAt, Guid? CreatedBy)
{
    internal static FileVersionResponse From(FileVersion v) =>
        new(v.Number, v.IsCurrent, v.FileName, v.MediaType, v.Size, v.Sha256, v.Source, v.CreatedAt, v.CreatedBy);
}

public sealed record FileVersionList([property: JsonPropertyName("value")] IReadOnlyList<FileVersionResponse> Value);

/// <summary>An existing document with the same content (DOC-10), among those the caller can read.</summary>
public sealed record DuplicateResponse(Guid WorkspaceId, Guid ListId, Guid ItemId, string? Title);

/// <summary>An uploaded document: the library item and its current file.</summary>
public sealed record DocumentResponse(
    Guid WorkspaceId, Guid ListId, Guid ItemId, uint Version, JsonObject Fields, FileVersionResponse File, IReadOnlyList<DuplicateResponse> Duplicates);

public sealed record LibrarySettingsResponse(Guid ListId, DuplicatePolicy DuplicatePolicy);

public sealed record LibrarySettingsRequest(DuplicatePolicy DuplicatePolicy);

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
        file.MapGet("", DownloadAsync).RequireScope(DocumentScopes.Read).WithName("DownloadFile");
        file.MapPut("", ReplaceAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("ReplaceFile")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit);
        file.MapGet("/versions", VersionsAsync).RequireScope(DocumentScopes.Read).WithName("ListFileVersions");
        file.MapGet("/versions/{number:int}", DownloadVersionAsync).RequireScope(DocumentScopes.Read).WithName("DownloadFileVersion");
        file.MapPost("/versions/{number:int}/restore", RestoreAsync).RequireScope(DocumentScopes.Write).WithName("RestoreFileVersion");

        endpoints.MapV1Group("me/inbox", "Documents")
            .MapPost("/documents", UploadToInboxAsync).RequireScope(DocumentScopes.Write).DisableAntiforgery().WithName("UploadToInbox")
            .WithMetadata(new RequestSizeLimitAttribute(limit)).WithFormOptions(multipartBodyLengthLimit: limit);
    }

    /// <summary>Multipart upload (<c>file</c>, optional <c>title</c> and <c>contentTypeId</c>) that creates a library item.</summary>
    private static Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        Guid workspaceId, Guid listId, IFormFile? file, [FromForm] string? title, [FromForm] Guid? contentTypeId,
        DocumentService documents, CancellationToken ct) =>
        documents.UploadAsync(workspaceId, listId, file, title, contentTypeId, ct);

    /// <summary>Uploads into the caller's Inbox library (LST-07), created on first use.</summary>
    private static async Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadToInboxAsync(
        IFormFile? file, [FromForm] string? title, IListItemStore items, DocumentService documents, CancellationToken ct)
    {
        var home = await items.EnsureHomeAsync(ct);
        return await documents.UploadAsync(home.WorkspaceId, home.InboxListId, file, title, null, ct);
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
        Guid workspaceId, Guid listId, Guid itemId, IFormFile? file, HttpRequest http, DocumentService documents, CancellationToken ct) =>
        documents.ReplaceAsync(workspaceId, listId, itemId, file, http.Headers.IfMatch.ToString(), ct);

    /// <summary>Makes an earlier version current again, as a new version (the history is kept).</summary>
    private static Task<Results<Ok<FileVersionResponse>, ProblemHttpResult>> RestoreAsync(
        Guid workspaceId, Guid listId, Guid itemId, int number, DocumentService documents, CancellationToken ct) =>
        documents.RestoreAsync(workspaceId, listId, itemId, number, ct);

    private static async Task<Results<Ok<LibrarySettingsResponse>, ProblemHttpResult>> GetSettingsAsync(
        Guid workspaceId, Guid listId, IListItemStore items, DocumentsDbContext db, HttpResponse response, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        if (list is not { IsLibrary: true })
        {
            return ApiErrors.NotFound("The library was not found.");
        }

        var settings = await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(s => s.ListId == listId, ct);
        ETags.Set(response, settings?.Version ?? 0);
        return TypedResults.Ok(new LibrarySettingsResponse(listId, settings?.DuplicatePolicy ?? DuplicatePolicy.Warn));
    }

    /// <summary>Changes the library's document settings (needs Manage on the library).</summary>
    private static async Task<Results<Ok<LibrarySettingsResponse>, ProblemHttpResult>> UpdateSettingsAsync(
        Guid workspaceId, Guid listId, LibrarySettingsRequest request, IListItemStore items, DocumentsDbContext db,
        HttpRequest http, HttpResponse response, CancellationToken ct)
    {
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

        settings.DuplicatePolicy = request.DuplicatePolicy;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, settings.Version);
        return TypedResults.Ok(new LibrarySettingsResponse(listId, settings.DuplicatePolicy));
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
}

/// <summary>Upload, replace and restore: spool, check, store once, then create the item and the version.</summary>
internal sealed class DocumentService(
    IListItemStore items, DocumentsDbContext db, FileIntake intake, IOptions<DocumentsOptions> options)
{
    private const int MaxDuplicates = 20;

    public static string ETag(FileVersion version) => $"\"{version.Sha256}\"";

    public static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    public async Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        Guid workspaceId, Guid listId, IFormFile? file, string? title, Guid? contentTypeId, CancellationToken ct)
    {
        if (file is null)
        {
            return MissingFile();
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
        var created = await items.CreateAsync(workspaceId, listId, fields, contentTypeId, ct);
        if (!created.Succeeded)
        {
            return ItemProblem(created);
        }

        var item = created.Item!;
        var version = NewVersion(item, stored, fileName, number: 1, "upload");
        db.FileVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created(
            $"{ApiRoutes.V1}/workspaces/{workspaceId}/lists/{listId}/items/{item.Id}/file",
            Response(item, version, policy == DuplicatePolicy.Warn ? duplicates : []));
    }

    public async Task<Results<Ok<DocumentResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid workspaceId, Guid listId, Guid itemId, IFormFile? file, string? ifMatch, CancellationToken ct)
    {
        if (file is null)
        {
            return MissingFile();
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
        var version = await AddVersionAsync(item, current, stored, FileName(file.FileName, spooled.MediaType!), "upload", ct);
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
        var version = await AddVersionAsync(item, current, stored, source.FileName, "restore", ct);
        return version is null
            ? ApiErrors.Conflict("concurrentChange", "The file was changed at the same time; try again.")
            : TypedResults.Ok(FileVersionResponse.From(version));
    }

    /// <summary>Adds the next version and makes it current; null when another change won the race.</summary>
    private async Task<FileVersion?> AddVersionAsync(ListItemData item, FileVersion? current, StoredFile stored, string fileName, string source, CancellationToken ct)
    {
        var number = (await db.FileVersions.Where(v => v.ItemId == item.Id).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
        current?.IsCurrent = false;
        var version = NewVersion(item, stored, fileName, number, source);
        db.FileVersions.Add(version);
        try
        {
            await db.SaveChangesAsync(ct);
            return version;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
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

    private static FileVersion NewVersion(ListItemData item, StoredFile stored, string fileName, int number, string source) => new()
    {
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
