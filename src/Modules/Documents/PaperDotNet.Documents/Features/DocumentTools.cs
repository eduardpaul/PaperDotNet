using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Documents.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Mcp.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>Library files for AI assistants (API-08). Bytes travel as base64 because MCP arguments are JSON.</summary>
internal static class DocumentTools
{
    public static McpToolResult From(IResult result) => result switch
    {
        Created<DocumentResponse> { Value: { } created } => McpToolResult.FromJson(Payload(created)),
        Ok<DocumentResponse> { Value: { } replaced } => McpToolResult.FromJson(Payload(replaced)),
        ValidationProblem validation => McpToolResult.Error(string.Join(
            " ", validation.ProblemDetails.Errors.SelectMany(error => error.Value.Select(message => $"{error.Key}: {message}")))),
        ProblemHttpResult problem => McpToolResult.Error(problem.ProblemDetails.Detail ?? problem.ProblemDetails.Title ?? "The file was rejected."),
        _ => McpToolResult.Error("The file was rejected."),
    };

    public static byte[]? Decode(string content, long maxBytes, out string? error)
    {
        var payload = content.Trim();
        const string marker = "base64,";
        var markerAt = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && markerAt >= 0)
        {
            payload = payload[(markerAt + marker.Length)..];
        }

        payload = payload.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (payload.Length > ((maxBytes + 2) / 3 * 4) + 8)
        {
            error = $"Files may have at most {maxBytes} bytes.";
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length > maxBytes)
            {
                error = $"Files may have at most {maxBytes} bytes.";
                return null;
            }

            error = null;
            return bytes;
        }
        catch (FormatException)
        {
            error = "contentBase64 must be base64-encoded file bytes.";
            return null;
        }
    }

    public static FormFile File(Stream content, string fileName) => new(content, 0, content.Length, "file", fileName)
    {
        Headers = new HeaderDictionary(),
        ContentType = "application/octet-stream",
    };

    private static object Payload(DocumentResponse document) => new
    {
        document.WorkspaceId,
        document.ListId,
        itemId = document.ItemId,
        document.Version,
        document.Fields,
        file = FileInfo(document.File),
        duplicates = document.Duplicates.Select(duplicate => new { duplicate.WorkspaceId, duplicate.ListId, duplicate.ItemId, duplicate.Title }),
    };

    public static object FileInfo(FileVersionResponse file) => new
    {
        file.Number,
        file.FileName,
        file.MediaType,
        file.Size,
        file.Sha256,
        file.PageCount,
    };
}

internal sealed class UploadDocumentTool(DocumentService documents, IOptions<DocumentsOptions> options) : IMcpTool
{
    public string Name => "upload_document";

    public string Description =>
        "Uploads a PDF, TIFF, JPEG or PNG into a library (isLibrary from list_lists) and creates the item. " +
        "contentBase64 is the raw file, or a data URL. Text extraction starts automatically. " +
        "folderId places the file in a folder from create_folder or ensure_folder. " +
        "Only these file types are accepted; the content is checked, not the extension.";

    public JsonElement InputSchema { get; } = McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "Library id.", true),
        ("fileName", "string", "File name, for example invoice.pdf.", true),
        ("contentBase64", "string", "Base64-encoded file bytes.", true),
        ("title", "string", "Item title. Defaults to the file name.", false),
        ("folderId", "string", "Folder id. Omit for the library root.", false),
        ("contentTypeId", "string", "Content type id from describe_list (default: the library's first).", false),
        ("languages", "string", "OCR languages, for example deu+eng. Omit for the library default.", false));

    public string? RequiredScope => DocumentScopes.Write;

    public bool IsReadOnly => false;

    public async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var bytes = DocumentTools.Decode(arguments.GetRequiredString("contentBase64"), options.Value.MaxFileSize, out var error);
        if (bytes is null)
        {
            return McpToolResult.Error(error!);
        }

        await using var content = new MemoryStream(bytes);
        var file = DocumentTools.File(content, arguments.GetRequiredString("fileName"));
        var result = await documents.UploadAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), file, arguments.GetString("title"),
            arguments.GetGuid("contentTypeId"), arguments.GetString("languages"), cancellationToken, arguments.GetGuid("folderId"));
        return DocumentTools.From(result.Result);
    }
}

internal sealed class ReplaceDocumentTool(DocumentService documents, IOptions<DocumentsOptions> options) : IMcpTool
{
    public string Name => "replace_document";

    public string Description =>
        "Stores a new version of a library item's file. Pass sha256 from get_file so you do not replace a version you have not seen. " +
        "contentBase64 is the new PDF, TIFF, JPEG or PNG.";

    public JsonElement InputSchema { get; } = McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "Library id.", true),
        ("itemId", "string", "Document item id.", true),
        ("fileName", "string", "File name of the new version.", true),
        ("contentBase64", "string", "Base64-encoded file bytes.", true),
        ("sha256", "string", "sha256 of the current file from get_file (optional).", false),
        ("languages", "string", "OCR languages, for example deu+eng.", false));

    public string? RequiredScope => DocumentScopes.Write;

    public bool IsReadOnly => false;

    public async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var bytes = DocumentTools.Decode(arguments.GetRequiredString("contentBase64"), options.Value.MaxFileSize, out var error);
        if (bytes is null)
        {
            return McpToolResult.Error(error!);
        }

        var sha256 = arguments.GetString("sha256");
        await using var content = new MemoryStream(bytes);
        var file = DocumentTools.File(content, arguments.GetRequiredString("fileName"));
        var result = await documents.ReplaceAsync(
            arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), arguments.GetRequiredGuid("itemId"),
            file, arguments.GetString("languages"), sha256 is null ? null : $"\"{sha256}\"", cancellationToken);
        return DocumentTools.From(result.Result);
    }
}

internal sealed class GetFileTool(IListItemStore items, DocumentsDbContext db) : IMcpTool
{
    public string Name => "get_file";

    public string Description =>
        "The current file of a library item: name, media type, size, sha256, page count and whether text extraction has finished. " +
        "Use sha256 with replace_document. Use read_document for the text.";

    public JsonElement InputSchema { get; } = McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "Library id.", true),
        ("itemId", "string", "Document item id.", true));

    public string? RequiredScope => DocumentScopes.Read;

    public bool IsReadOnly => true;

    public async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var version = await CurrentAsync(items, db, arguments, cancellationToken);
        return version switch
        {
            null => McpToolResult.Error("The item was not found (or you cannot read it)."),
            { File: null } => McpToolResult.Error("The item has no file."),
            { File: { } file } => McpToolResult.FromJson(new { itemId = version.ItemId, file = DocumentTools.FileInfo(FileVersionResponse.From(file)) }),
        };
    }

    internal static async Task<CurrentFile?> CurrentAsync(IListItemStore items, DocumentsDbContext db, McpArguments arguments, CancellationToken cancellationToken)
    {
        var itemId = arguments.GetRequiredGuid("itemId");
        var item = await items.GetAsync(arguments.GetRequiredGuid("workspaceId"), arguments.GetRequiredGuid("listId"), itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var file = await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(version => version.ItemId == itemId && version.IsCurrent, cancellationToken);
        return new CurrentFile(itemId, file);
    }

    internal sealed record CurrentFile(Guid ItemId, FileVersion? File);
}

internal sealed class ReadDocumentTool(IListItemStore items, DocumentsDbContext db) : IMcpTool
{
    private const int DefaultPages = 5;
    private const int MaxPages = 20;
    private const int DefaultCharacters = 24_000;
    private const int MaxCharacters = 100_000;

    public string Name => "read_document";

    public string Description =>
        "Reads the extracted text of a document, one page at a time. " +
        "The text comes from the library's workflows (reading the text layer, OCR); a file they have not read yet has no pages, so call again later. " +
        $"fromPage starts at 1. At most maxPages pages ({DefaultPages} by default, up to {MaxPages}) and maxCharacters characters are returned. " +
        "When nextPage is present, call again with fromPage set to it. A single page longer than maxCharacters is cut off.";

    public JsonElement InputSchema { get; } = McpSchema.ObjectSchema(
        ("workspaceId", "string", "Workspace id.", true),
        ("listId", "string", "Library id.", true),
        ("itemId", "string", "Document item id.", true),
        ("fromPage", "integer", "First page to return (1-based, default 1).", false),
        ("maxPages", "integer", $"Maximum pages in this call (1-{MaxPages}, default {DefaultPages}).", false),
        ("maxCharacters", "integer", $"Maximum characters in this call (default {DefaultCharacters}).", false));

    public string? RequiredScope => DocumentScopes.Read;

    public bool IsReadOnly => true;

    public async Task<McpToolResult> CallAsync(McpArguments arguments, CancellationToken cancellationToken)
    {
        var current = await GetFileTool.CurrentAsync(items, db, arguments, cancellationToken);
        if (current is null)
        {
            return McpToolResult.Error("The item was not found (or you cannot read it).");
        }

        if (current.File is not { } file)
        {
            return McpToolResult.Error("The item has no file.");
        }

        var fromPage = Math.Max(1, arguments.GetInt32("fromPage") ?? 1);
        var maxPages = Math.Clamp(arguments.GetInt32("maxPages") ?? DefaultPages, 1, MaxPages);
        var maxCharacters = Math.Clamp(arguments.GetInt32("maxCharacters") ?? DefaultCharacters, 1, MaxCharacters);
        var stored = await db.Pages.AsNoTracking()
            .Where(page => page.StoredFileId == file.StoredFileId && page.PageNumber >= fromPage)
            .OrderBy(page => page.PageNumber)
            .Take(maxPages)
            .Select(page => new { page.PageNumber, page.Text })
            .ToListAsync(cancellationToken);
        var pages = new List<object>();
        var used = 0;
        int? nextPage = null;
        var truncated = false;
        foreach (var page in stored)
        {
            var room = maxCharacters - used;
            if (pages.Count > 0 && page.Text.Length > room)
            {
                nextPage = page.PageNumber;
                break;
            }

            var text = page.Text.Length > room ? page.Text[..room] : page.Text;
            truncated = text.Length < page.Text.Length;
            pages.Add(new { page = page.PageNumber, text });
            used += text.Length;
            if (truncated)
            {
                nextPage = page.PageNumber + 1;
                break;
            }
        }

        if (nextPage is null && stored.Count == maxPages)
        {
            var last = stored[^1].PageNumber;
            if (await db.Pages.AsNoTracking().AnyAsync(page => page.StoredFileId == file.StoredFileId && page.PageNumber > last, cancellationToken))
            {
                nextPage = last + 1;
            }
        }

        return McpToolResult.FromJson(new
        {
            file.FileName,
            file.PageCount,
            pages,
            nextPage,
            truncated,
            message = pages.Count == 0
                ? "No text is stored for this file yet: the library's workflows (\"Read the text\", OCR) have not read it, or are off."
                : null,
        });
    }
}
