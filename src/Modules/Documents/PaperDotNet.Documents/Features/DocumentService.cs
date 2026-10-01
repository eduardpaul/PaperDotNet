using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// Upload, replace and restore: spool, check, store once, then create the item and the version. The item store acts as
/// the request's caller; the tenant and the author of changes are the <see cref="ChangeActor"/> passed in.
/// </summary>
internal sealed class DocumentService(IListItemStore items, DocumentsDbContext db, FileIntake intake, DocumentEvents events, IOptions<DocumentsOptions> options)
{
    private const int MaxDuplicates = 20;

    public static string ETag(FileVersion version) => $"\"{version.Sha256}\"";

    public static ProblemHttpResult Forbidden() =>
        ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You do not have permission for this action in the workspace.");

    public async Task<Results<Created<DocumentResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        ChangeActor actor, Guid workspaceId, Guid listId, IFormFile? file, string? title, Guid? contentTypeId, string? languages, Guid? folderId,
        CancellationToken ct)
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
            return ApiErrors.BadRequest("notALibrary", "Files can only be uploaded into libraries.");
        }

        if (list.Access < WorkspaceAccessLevel.Contribute)
        {
            return Forbidden();
        }

        if (folderId is { } folder && await items.GetAsync(workspaceId, listId, folder, ct) is not { IsFolder: true })
        {
            return ApiErrors.Validation("folderId", "No folder with this id in the library.");
        }

        await using var spooled = await SpoolAsync(file, ct);
        if (Check(spooled) is { } invalid)
        {
            return invalid;
        }

        var duplicates = await DuplicatesAsync(actor.TenantId, spooled.Sha256, exceptItem: null, ct);
        var policy = await PolicyAsync(actor.TenantId, listId, ct);
        if (policy == DuplicatePolicies.Block && duplicates.Count > 0)
        {
            return DuplicateBlocked(duplicates);
        }

        var stored = await intake.StoreAsync(actor.TenantId, spooled, ct);
        var fileName = FileName(file.FileName, spooled.MediaType!);
        var fields = new JsonObject { ["title"] = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title };
        var created = await items.CreateAsync(workspaceId, listId, fields, contentTypeId, folderId, ct);
        if (!created.Succeeded)
        {
            return ItemProblem(created);
        }

        var item = created.Item!;
        var version = NewVersion(actor.TenantId, item, stored, fileName, number: 1, "upload", Languages(languages));
        db.FileVersions.Add(version);
        await db.SaveChangesAsync(ct);
        await events.AddedAsync(actor, version, newDocument: true, ct);
        return TypedResults.Created(
            $"/v1.0/workspaces/{workspaceId}/lists/{listId}/items/{item.Id}/file",
            Response(item, version, policy == DuplicatePolicies.Warn ? duplicates : []));
    }

    public async Task<Results<Ok<DocumentResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        ChangeActor actor, Guid workspaceId, Guid listId, Guid itemId, IFormFile? file, string? languages, string? ifMatch, CancellationToken ct)
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

        var current = await DocumentQueries.CurrentAsync(db, actor.TenantId, itemId, ct);
        if (!string.IsNullOrEmpty(ifMatch) && (current is null || ifMatch != ETag(current)))
        {
            return ApiErrors.PreconditionFailed();
        }

        await using var spooled = await SpoolAsync(file, ct);
        if (Check(spooled) is { } invalid)
        {
            return invalid;
        }

        var duplicates = await DuplicatesAsync(actor.TenantId, spooled.Sha256, exceptItem: itemId, ct);
        var policy = await PolicyAsync(actor.TenantId, listId, ct);
        if (policy == DuplicatePolicies.Block && duplicates.Count > 0)
        {
            return DuplicateBlocked(duplicates);
        }

        var stored = await intake.StoreAsync(actor.TenantId, spooled, ct);
        var version = await AddVersionAsync(actor, item, current, stored, FileName(file.FileName, spooled.MediaType!), "upload", Languages(languages), ct);
        return version is null
            ? ApiErrors.Conflict("concurrentChange", "The file was changed at the same time; try again.")
            : TypedResults.Ok(Response(item, version, policy == DuplicatePolicies.Warn ? duplicates : []));
    }

    public async Task<Results<Ok<FileVersionResponse>, ProblemHttpResult>> RestoreAsync(
        ChangeActor actor, Guid workspaceId, Guid listId, Guid itemId, int number, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        var source = item is null ? null : await DocumentQueries.VersionAsync(db, actor.TenantId, itemId, number, ct);
        if (source is null)
        {
            return ApiErrors.NotFound();
        }

        if (item!.Access < WorkspaceAccessLevel.Contribute)
        {
            return Forbidden();
        }

        var current = await DocumentQueries.CurrentAsync(db, actor.TenantId, itemId, ct);
        var stored = await DocumentQueries.StoredFileAsync(db, actor.TenantId, source.StoredFileId, ct);
        if (stored is null)
        {
            return ApiErrors.Problem(StatusCodes.Status500InternalServerError, "fileContentMissing", "The stored content of the file is missing.");
        }

        var version = await AddVersionAsync(actor, item, current, stored, source.FileName, "restore", null, ct);
        return version is null
            ? ApiErrors.Conflict("concurrentChange", "The file was changed at the same time; try again.")
            : TypedResults.Ok(FileVersionResponse.From(version));
    }

    /// <summary>
    /// Adds the next version and makes it current; null when another change won the race. The new version keeps the current
    /// one's languages unless <paramref name="languages"/> are given.
    /// </summary>
    internal async Task<FileVersion?> AddVersionAsync(
        ChangeActor actor, ListItemData item, FileVersion? current, StoredFile stored, string fileName, string source, string? languages, CancellationToken ct,
        bool announce = true)
    {
        var number = (await DocumentQueries.LastNumberAsync(db, actor.TenantId, item.Id, ct) ?? 0) + 1;
        current?.IsCurrent = false;
        var version = NewVersion(actor.TenantId, item, stored, fileName, number, source, languages ?? current?.Languages);
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

        if (announce)
        {
            await events.AddedAsync(actor, version, newDocument: current is null, ct);
        }

        return version;
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
    private async Task<List<DuplicateResponse>> DuplicatesAsync(Guid tenantId, string sha256, Guid? exceptItem, CancellationToken ct)
    {
        var result = new List<DuplicateResponse>();
        foreach (var candidate in await DocumentQueries.CurrentWithHashAsync(db, tenantId, sha256, MaxDuplicates + 1, ct))
        {
            if (candidate.ItemId != exceptItem && result.Count < MaxDuplicates
                && await items.GetAsync(candidate.WorkspaceId, candidate.ListId, candidate.ItemId, ct) is { } visible)
            {
                result.Add(new DuplicateResponse(candidate.WorkspaceId, candidate.ListId, candidate.ItemId,
                    visible.Fields["title"] is JsonValue title && title.TryGetValue<string>(out var text) ? text : null));
            }
        }

        return result;
    }

    private async Task<string> PolicyAsync(Guid tenantId, Guid listId, CancellationToken ct) =>
        (await DocumentQueries.SettingsAsync(db, tenantId, listId, ct))?.DuplicatePolicy ?? DuplicatePolicies.Warn;

    private static string? Languages(string? languages) => string.IsNullOrWhiteSpace(languages) ? null : languages.Trim();

    private static ValidationProblem? InvalidLanguages(string? languages) =>
        Languages(languages) is { } value && !DocumentText.IsValidLanguageList(value)
            ? ApiErrors.Validation("languages", "Tesseract language codes joined with '+', e.g. 'deu+eng'.")
            : null;

    internal static FileVersion NewVersion(Guid tenantId, ListItemData item, StoredFile stored, string fileName, int number, string source, string? languages) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        Languages = languages,
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
    internal static string FileName(string? clientName, string mediaType)
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

    private static ValidationProblem MissingFile() => ApiErrors.Validation("file", "A file is required (multipart/form-data, field 'file').");

    private static ProblemHttpResult DuplicateBlocked(List<DuplicateResponse> duplicates) =>
        ApiErrors.Conflict("duplicateFile", $"The library does not accept duplicates; the same file exists as item {duplicates[0].ItemId}.");

    private static ProblemHttpResult ItemProblem(ListItemResult result) => result.Status switch
    {
        ListItemStatus.NotFound => ApiErrors.NotFound(),
        ListItemStatus.Forbidden => Forbidden(),
        ListItemStatus.Invalid => ApiErrors.BadRequest("invalidFields",
            string.Join(" ", (result.Errors ?? new Dictionary<string, string[]>()).Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"))),
        _ => ApiErrors.Conflict("rejected", result.Message ?? "The change was rejected."),
    };
}
