using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;
using SkiaSharp;
using UglyToad.PdfPig;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// Announces files to workflows (ADR-0038): an upload, a new version or an imported current version only stores the file
/// and raises <c>document.added</c>. Text, thumbnails, page images and OCR are the library's workflows on it.
/// </summary>
internal sealed class DocumentEvents(IWorkflowTriggers triggers)
{
    public Task AddedAsync(FileVersion version, bool newDocument, CancellationToken ct) =>
        triggers.RaiseAsync(WorkflowTriggers.DocumentAdded, version.WorkspaceId, new WorkflowItem(version.WorkspaceId, version.ListId, version.ItemId),
            new JsonObject
            {
                ["version"] = version.Number,
                ["mediaType"] = version.MediaType,
                ["fileName"] = version.FileName,
                ["newDocument"] = newDocument,
            },
            AddedEventId(version.Id), ct);

    /// <summary>The event id of <c>document.added</c> for a version: announcing it again starts nothing twice.</summary>
    internal static Guid AddedEventId(Guid versionId)
    {
        Span<byte> input = stackalloc byte[17];
        versionId.TryWriteBytes(input);
        input[16] = (byte)'a';
        var bytes = SHA256.HashData(input)[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80); // Version 8 (name-based, custom).
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant.
        return new Guid(bytes);
    }
}

/// <summary>Whether a file has text, the languages to read it in, and its page texts.</summary>
internal static partial class DocumentText
{
    /// <summary>Below this many characters per page a PDF counts as a scan.</summary>
    public const int MinTextPerPage = 20;

    /// <summary>Whether page texts are enough to count as text (not a scan).</summary>
    public static bool Enough(IReadOnlyCollection<string> pages) =>
        pages.Count > 0 && pages.Sum(p => p.Trim().Length) >= MinTextPerPage * pages.Count;

    /// <summary>The saved page texts of a stored file, in order.</summary>
    public static Task<List<string>> PagesAsync(DocumentsDbContext db, Guid storedFileId, CancellationToken ct) =>
        db.Pages.AsNoTracking().Where(p => p.StoredFileId == storedFileId).OrderBy(p => p.PageNumber).Select(p => p.Text).ToListAsync(ct);

    /// <summary>Whether the version has text: an OCR result, or enough saved page texts.</summary>
    public static async Task<bool> HasTextAsync(DocumentsDbContext db, FileVersion version, CancellationToken ct) =>
        version.Source == "ocr" || Enough(await PagesAsync(db, version.StoredFileId, ct));

    /// <summary>Stores page texts once per content (identical files share them).</summary>
    public static async Task SavePagesAsync(DocumentsDbContext db, Guid storedFileId, IReadOnlyList<string> pages, CancellationToken ct)
    {
        if (pages.Count == 0 || await db.Pages.AnyAsync(p => p.StoredFileId == storedFileId, ct))
        {
            return;
        }

        db.Pages.AddRange(pages.Select((text, i) => new StoredFilePage { StoredFileId = storedFileId, PageNumber = i + 1, Text = text.Trim() }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>DOC-17: the request, the file, the library, then the uploader's (or the organization's) document languages.</summary>
    public static async Task<string> LanguagesAsync(
        DocumentsDbContext db, IUserPreferences preferences, FileVersion version, string? requested, CancellationToken ct)
    {
        var settings = await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(s => s.ListId == version.ListId, ct);
        return requested ?? version.Languages ?? settings?.OcrLanguages
            ?? (version.CreatedBy is { } uploader
                ? (await preferences.GetAsync(uploader, ct)).DocumentLanguages
                : (await preferences.GetDefaultsAsync(ct)).DocumentLanguages);
    }

    public static string FirstLanguage(string languages) => languages.Split('+')[0];

    /// <summary>Tesseract language list: codes of letters and underscores joined with <c>+</c>.</summary>
    public static bool IsValidLanguageList(string value) => LanguageList().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z_]{1,30}(\\+[a-z][a-z_]{1,30}){0,5}$")]
    private static partial Regex LanguageList();
}

/// <summary>
/// Payload of the <c>documents.ocrFile</c> operation: <c>Force</c> runs OCR even when the file has text; <c>WaitKey</c> is
/// the wait of the step that started it.
/// </summary>
public sealed record OcrFile(Guid VersionId, bool Force = false, string? Languages = null, string? WaitKey = null);

/// <summary>
/// OCR of a file version (DOC-07, ADR-0038): images and PDFs without usable text are recognized and the result is stored
/// as a new, searchable PDF version (the original stays), with page texts for search. Started by <c>document.ocr</c>
/// steps (the built-in "Recognize text", or any workflow); completes the steps' waits when it finishes.
/// </summary>
internal sealed partial class DocumentOcr(
    DocumentsDbContext db,
    IBlobStore blobs,
    FileIntake intake,
    OcrEngine ocr,
    PageRenderer renderer,
    IListItemStore items,
    ILiveEvents live,
    IUserPreferences preferences,
    IWorkflowBookmarks bookmarks,
    DocumentEvents events,
    ITenantContext tenant,
    ICurrentUser user,
    ILogger<DocumentOcr> logger) : OperationHandler<OcrFile>
{
    public const string OperationType = "documents.ocrFile";

    /// <summary>The waits of <c>document.ocr</c> steps for this operation's version (data: <c>version</c>).</summary>
    public const string WaitKind = "document.ocr";

    public override string Type => OperationType;

    protected override async Task<object?> ExecuteAsync(OcrFile payload, IOperationProgress progress, CancellationToken ct)
    {
        var version = await db.FileVersions.FirstOrDefaultAsync(v => v.Id == payload.VersionId, ct);
        if (version is null)
        {
            return null; // The item was purged meanwhile.
        }

        if (!version.IsCurrent || (!payload.Force && await DocumentText.HasTextAsync(db, version, ct)))
        {
            // A newer version came, or the file has text: nothing to recognize.
            await CompleteWaitsAsync(version, payload.WaitKey, new JsonObject { ["ocr"] = false }, ct);
            return new OcrResult(version.Number, version.PageCount, false);
        }

        OcrResult result;
        try
        {
            result = await RecognizeAsync(version, payload, progress, ct);
        }
#pragma warning disable CA1031 // Any failure goes to the waiting steps; the operation fails too.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogOcrFailed(ex, version.Id);
            db.ChangeTracker.Clear();
            await CompleteWaitsAsync(version, payload.WaitKey, new JsonObject { ["error"] = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message }, ct);
            throw;
        }

        await CompleteWaitsAsync(version, payload.WaitKey, new JsonObject { ["ocr"] = true, ["version"] = result.Number }, ct);
        live.Publish(DocumentLiveEvents.Changed(tenant, user, version, "ocr"));
        return result;
    }

    /// <summary>
    /// Completes the wait of the step that started it (kept when the step has not saved it yet), and those of other
    /// <c>document.ocr</c> steps waiting for the version.
    /// </summary>
    private async Task CompleteWaitsAsync(FileVersion version, string? waitKey, JsonObject payload, CancellationToken ct)
    {
        var id = version.Id.ToString();
        var keys = waitKey is null ? new List<string>() : [waitKey];
        for (var skip = 0; ; skip += 100)
        {
            var page = await bookmarks.ListOpenAsync(WaitKind, version.WorkspaceId, skip, 100, ct);
            keys.AddRange(page.Where(w => w.Data?["version"]?.GetValue<string>() == id).Select(w => w.Key));
            if (page.Count < 100)
            {
                break;
            }
        }

        foreach (var key in keys.Distinct())
        {
            await bookmarks.CompleteAsync(WaitKind, key, payload.DeepClone().AsObject(), ct);
        }
    }

    /// <summary>The operation's result: the current version (the OCR result), its pages and whether OCR ran.</summary>
    private sealed record OcrResult(int Number, int? PageCount, bool Ocr);

    private async Task<OcrResult> RecognizeAsync(FileVersion version, OcrFile payload, IOperationProgress progress, CancellationToken ct)
    {
        var stored = await db.StoredFiles.FirstAsync(f => f.Id == version.StoredFileId, ct);
        var languages = await DocumentText.LanguagesAsync(db, preferences, version, payload.Languages, ct);
        var work = Directory.CreateTempSubdirectory("pdn_ocr_");
        try
        {
            var source = Path.Combine(work.FullName, "source");
            await using (var content = await blobs.OpenReadAsync(stored.BlobKey, ct) ?? throw new InvalidOperationException("The stored content of the file is missing."))
            await using (var file = File.Create(source))
            {
                await content.CopyToAsync(file, ct);
            }

            var input = source;
            if (version.MediaType == FileTypes.Pdf)
            {
                input = await renderer.RenderForOcrAsync(source, TextExtractor.PdfPages(source).Count, work.FullName, ct);
            }

            await progress.ReportAsync(10, ct);
            var (pdf, text) = await ocr.RecognizeAsync(input, languages, Path.Combine(work.FullName, "ocr"), ct);
            await progress.ReportAsync(80, ct);
            var current = await AddOcrVersionAsync(version, pdf, text, languages, ct);
            await db.SaveChangesAsync(ct);
            await items.ReindexAsync(version.ItemId, ct);
            if (current is not null)
            {
                // A new version like any other: the library's workflows make its thumbnail and pages, and read its text.
                await events.AddedAsync(current, newDocument: false, ct);
            }

            return new OcrResult((current ?? version).Number, (current ?? version).PageCount, true);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Stores the OCR result as the new current version (only if <paramref name="version"/> is still
    /// current: a newer upload wins). Returns the new version, or null.
    /// </summary>
    private async Task<FileVersion?> AddOcrVersionAsync(FileVersion version, string pdfPath, IReadOnlyList<string> pages, string languages, CancellationToken ct)
    {
        await using var spooled = await SpoolFileAsync(pdfPath, ct);
        var stored = await intake.StoreAsync(spooled, ct);
        await DocumentText.SavePagesAsync(db, stored.Id, pages, ct);
        version.PageCount ??= pages.Count;
        version.TextLanguage = DocumentText.FirstLanguage(languages);
        if (!version.IsCurrent)
        {
            return null;
        }

        var number = await db.FileVersions.Where(v => v.ItemId == version.ItemId).MaxAsync(v => v.Number, ct) + 1;
        version.IsCurrent = false;
        var ocrVersion = new FileVersion
        {
            Id = Ids.New(),
            WorkspaceId = version.WorkspaceId,
            ListId = version.ListId,
            ItemId = version.ItemId,
            Number = number,
            IsCurrent = true,
            StoredFileId = stored.Id,
            Sha256 = stored.Sha256,
            Size = stored.Size,
            MediaType = stored.MediaType,
            FileName = Path.GetFileNameWithoutExtension(version.FileName) + ".pdf",
            Source = "ocr",
            PageCount = pages.Count,
            TextLanguage = DocumentText.FirstLanguage(languages),
            Languages = version.Languages,
        };
        db.FileVersions.Add(ocrVersion);
        return ocrVersion;
    }

    private static async Task<SpooledFile> SpoolFileAsync(string path, CancellationToken ct)
    {
        await using var content = File.OpenRead(path);
        return await FileIntake.SpoolAsync(content, long.MaxValue, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "OCR of file version {VersionId} failed.")]
    private partial void LogOcrFailed(Exception exception, Guid versionId);
}

/// <summary>The live event <c>document.changed</c>: a document workflow step made something new for the file (text, thumbnail, pages, OCR).</summary>
internal static class DocumentLiveEvents
{
    public static LiveEvent Changed(ITenantContext tenant, ICurrentUser user, FileVersion version, string what) =>
        new("document.changed", tenant.TenantId!.Value, user.UserId, new
        {
            version.WorkspaceId,
            version.ListId,
            version.ItemId,
            Version = version.Number,
            What = what,
        });
}

/// <summary>Text layer of PDFs, page by page (PdfPig), as words separated by spaces.</summary>
internal static class TextExtractor
{
    public static List<string> PdfPages(string path)
    {
        using var document = PdfDocument.Open(path);
        return document.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))).ToList();
    }
}

/// <summary>
/// OCR (ADR-0015, ADR-0034). <c>tesseract</c> is one CLI call over an image, a multi-page TIFF or a
/// list of page images. <c>glm</c> calls Ollama once per page. Both write a searchable PDF (page image
/// plus invisible text) and the plain text.
/// </summary>
internal sealed class OcrEngine(IOptions<DocumentsOptions> options, IHttpClientFactory http)
{
    public async Task<(string Pdf, IReadOnlyList<string> Pages)> RecognizeAsync(string input, string languages, string outputBase, CancellationToken ct)
    {
        if (GlmOcr.Uses(options.Value.Engine))
        {
            return await new GlmOcr(options.Value, http.CreateClient(GlmOcr.HttpClientName)).RecognizeAsync(input, outputBase, ct);
        }

        if (!UsesTesseract(options.Value.Engine))
        {
            throw new InvalidOperationException($"Unknown OCR engine '{options.Value.Engine}'. Use \"tesseract\" or \"glm\".");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.OcrTimeout);
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(options.Value.TesseractPath)
                .WithArguments([input, outputBase, "-l", languages, "pdf", "txt"])
                .WithEnvironmentVariables(e => e.Set("OMP_THREAD_LIMIT", "1"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"The OCR engine '{options.Value.TesseractPath}' is not installed.", ex);
        }

        var pdf = outputBase + ".pdf";
        if (result.ExitCode != 0 || !File.Exists(pdf))
        {
            var error = result.StandardError.Trim();
            throw new InvalidOperationException($"OCR failed: {(error.Length > 500 ? error[..500] : error)}");
        }

        var text = await File.ReadAllTextAsync(outputBase + ".txt", Encoding.UTF8, ct);
        var pages = text.Split('\f').ToList();
        if (pages.Count > 1 && string.IsNullOrWhiteSpace(pages[^1]))
        {
            pages.RemoveAt(pages.Count - 1); // Tesseract ends every page with a form feed.
        }

        return (pdf, pages);
    }

    private static bool UsesTesseract(string? engine) =>
        string.IsNullOrWhiteSpace(engine) || engine.Trim().Equals("tesseract", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Page images (DOC-04): renders PDF pages with PDFium (PDFtoImage) and resizes JPEG/PNG with SkiaSharp. What the
/// document workflows render (thumbnails, page previews; ADR-0038) is stored per content, page and width, and only that
/// is served; AI steps render pages without storing them.
/// </summary>
internal sealed class PageRenderer(DocumentsDbContext db, IBlobStore blobs, IOptions<DocumentsOptions> options)
{
    /// <summary>Widths rendered (thumbnail, preview, large); requests are rounded up to one of them.</summary>
    public static readonly int[] Widths = [200, 800, 1600];

    /// <summary>The widths of page previews (<c>document.renderPages</c>); thumbnails are the first width.</summary>
    public static readonly int[] PageWidths = [800, 1600];

    // PDFium is not thread-safe.
    private static readonly SemaphoreSlim PdfiumLock = new(1, 1);

    /// <summary>PDFium ships for Linux, Windows and macOS (the container is Linux).</summary>
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("macos")]
    private static bool PdfiumSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static int WidthFor(int? requested) => Widths.FirstOrDefault(w => w >= (requested ?? Widths[0]), Widths[^1]);

    /// <summary>The stored JPEG of the page at the width, or null when no document workflow rendered it.</summary>
    public async Task<Stream?> OpenStoredAsync(FileVersion version, int page, int width, CancellationToken ct)
    {
        var stored = await db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == version.StoredFileId, ct);
        return await blobs.OpenReadAsync(Key(stored, page, width), ct);
    }

    private static string Key(StoredFile stored, int page, int width) => $"{stored.TenantId:N}/renders/{stored.Sha256[..2]}/{stored.Sha256}/p{page}-w{width}";

    /// <summary>
    /// A JPEG of the page, or null when the page does not exist or the type cannot be rendered (TIFF before OCR). With
    /// <paramref name="store"/> (document workflows) it is stored, so it is served; a stored one is reused.
    /// </summary>
    public async Task<byte[]?> RenderAsync(FileVersion version, int page, int width, bool store, CancellationToken ct)
    {
        var stored = await db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == version.StoredFileId, ct);
        var key = Key(stored, page, width);
        if (await blobs.OpenReadAsync(key, ct) is { } cached)
        {
            await using (cached)
            {
                using var copy = new MemoryStream();
                await cached.CopyToAsync(copy, ct);
                return copy.ToArray();
            }
        }

        byte[]? jpeg;
        await using (var content = await blobs.OpenReadAsync(stored.BlobKey, ct))
        {
            if (content is null)
            {
                return null;
            }

            jpeg = version.MediaType switch
            {
                FileTypes.Pdf => await RenderPdfPageAsync(content, page, width, ct),
                FileTypes.Jpeg or FileTypes.Png when page == 1 => ResizeImage(content, width),
                _ => null,
            };
        }

        if (jpeg is not null && store)
        {
            await blobs.WriteAsync(key, new MemoryStream(jpeg), ct);
        }

        return jpeg;
    }

    /// <summary>Renders every page for OCR and writes a list file Tesseract reads (one image path per line).</summary>
    public async Task<string> RenderForOcrAsync(string pdfPath, int pageCount, string directory, CancellationToken ct)
    {
        if (!PdfiumSupported)
        {
            throw new PlatformNotSupportedException("Rendering PDF pages needs Linux, Windows or macOS.");
        }

        var paths = new List<string>();
        var bytes = await File.ReadAllBytesAsync(pdfPath, ct);
        for (var i = 0; i < Math.Min(pageCount, options.Value.MaxOcrPages); i++)
        {
            var path = Path.Combine(directory, $"page-{i + 1:D4}.png");
            await PdfiumLock.WaitAsync(ct);
            try
            {
                await using var file = File.Create(path);
                PDFtoImage.Conversion.SavePng(file, bytes, i, password: null, new PDFtoImage.RenderOptions { Dpi = options.Value.OcrDpi, WithAnnotations = true });
            }
            finally
            {
                PdfiumLock.Release();
            }

            paths.Add(path);
        }

        var list = Path.Combine(directory, "pages.txt");
        await File.WriteAllLinesAsync(list, paths, ct);
        return list;
    }

    private static async Task<byte[]?> RenderPdfPageAsync(Stream content, int page, int width, CancellationToken ct)
    {
        if (!PdfiumSupported)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        await PdfiumLock.WaitAsync(ct);
        try
        {
            if (page < 1 || page > PDFtoImage.Conversion.GetPageCount(bytes))
            {
                return null;
            }

            using var output = new MemoryStream();
            PDFtoImage.Conversion.SaveJpeg(output, bytes, page - 1, password: null,
                new PDFtoImage.RenderOptions { Width = width, WithAspectRatio = true, WithAnnotations = true, BackgroundColor = SKColors.White });
            return output.ToArray();
        }
        finally
        {
            PdfiumLock.Release();
        }
    }

    private static byte[]? ResizeImage(Stream content, int width)
    {
        using var original = SKBitmap.Decode(content);
        if (original is null)
        {
            return null;
        }

        var height = Math.Max(1, (int)Math.Round(original.Height * (width / (double)original.Width)));
        using var resized = original.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var image = SKImage.FromBitmap(resized ?? original);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}

/// <summary>The pages of an item's current file as JPEGs for AI that reads images (1600 pixels wide, cached like previews).</summary>
internal sealed class DocumentPageImages(DocumentsDbContext db, PageRenderer renderer) : IItemPageImageSource
{
    public async Task<IReadOnlyList<ItemPageImage>> GetPageImagesAsync(Guid itemId, int maxPages, CancellationToken cancellationToken)
    {
        var version = await db.FileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ItemId == itemId && v.IsCurrent, cancellationToken);
        var images = new List<ItemPageImage>();
        for (var page = 1; version is not null && page <= maxPages; page++)
        {
            // Rendered for the step, not stored: a library without page previews can still send its pages to a model.
            var image = await renderer.RenderAsync(version, page, PageRenderer.Widths[^1], store: false, cancellationToken);
            if (image is null)
            {
                break;
            }

            images.Add(new ItemPageImage(page, "image/jpeg", image));
        }

        return images;
    }
}

/// <summary>Adds the text of an item's current file to its search document, with its language (SRC-05).</summary>
internal sealed class DocumentSearchContent(DocumentsDbContext db) : IItemSearchContributor
{
    public async Task<IReadOnlyDictionary<Guid, ItemSearchContent>> GetContentAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var ids = itemIds.ToList();
        var versions = await db.FileVersions.AsNoTracking()
            .Where(v => ids.Contains(v.ItemId) && v.IsCurrent)
            .Select(v => new { v.ItemId, v.StoredFileId, v.TextLanguage })
            .ToListAsync(cancellationToken);
        if (versions.Count == 0)
        {
            return new Dictionary<Guid, ItemSearchContent>();
        }

        var storedIds = versions.Select(v => v.StoredFileId).Distinct().ToList();
        var pages = (await db.Pages.AsNoTracking().Where(p => storedIds.Contains(p.StoredFileId)).OrderBy(p => p.PageNumber).ToListAsync(cancellationToken))
            .ToLookup(p => p.StoredFileId);
        return versions
            .Where(v => pages[v.StoredFileId].Any())
            .ToDictionary(
                v => v.ItemId,
                v => new ItemSearchContent(string.Join('\n', pages[v.StoredFileId].Select(p => p.Text)), FullTextLanguages.FromCode(v.TextLanguage))
                {
                    Pages = PageTexts(pages[v.StoredFileId].Select(p => (p.PageNumber, p.Text))),
                });
    }

    /// <summary>Texts indexed by page number (missing pages become empty).</summary>
    private static List<string> PageTexts(IEnumerable<(int Number, string Text)> pages)
    {
        var texts = new List<string>();
        foreach (var (number, text) in pages)
        {
            while (texts.Count < number - 1)
            {
                texts.Add(string.Empty);
            }

            texts.Add(text);
        }

        return texts;
    }
}
