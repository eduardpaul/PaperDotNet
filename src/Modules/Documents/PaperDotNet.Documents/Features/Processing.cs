using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
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
using SkiaSharp;
using UglyToad.PdfPig;

namespace PaperDotNet.Documents.Features;

/// <summary>Payload of the <c>documents.processFile</c> operation.</summary>
public sealed record ProcessFile(Guid VersionId, bool ForceOcr = false, string? Languages = null);

/// <summary>Starts processing of file versions as operations (DOC-09: status on the version and the operation).</summary>
internal sealed partial class ProcessingScheduler(DocumentsDbContext db, IOperations operations)
{
    /// <summary>Marks <paramref name="version"/> as scheduled and starts its processing.</summary>
    public async Task<Guid> ScheduleAsync(FileVersion version, bool forceOcr, string? languages, CancellationToken ct)
    {
        var operationId = await operations.StartAsync(DocumentProcessor.OperationType, new ProcessFile(version.Id, forceOcr, languages), ct);
        version.ProcessingStatus = ProcessingStatus.Scheduled;
        version.ProcessingError = null;
        version.OperationId = operationId;
        await db.SaveChangesAsync(ct);
        return operationId;
    }

    /// <summary>Schedules automatic processing when the library has it on.</summary>
    public async Task ScheduleIfAutomaticAsync(FileVersion version, CancellationToken ct)
    {
        var settings = await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(s => s.ListId == version.ListId, ct);
        if (settings?.AutoProcess ?? true)
        {
            await ScheduleAsync(version, forceOcr: false, languages: null, ct);
        }
    }

    /// <summary>Tesseract language list: codes of letters and underscores joined with <c>+</c>.</summary>
    public static bool IsValidLanguageList(string value) => LanguageList().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z_]{1,30}(\\+[a-z][a-z_]{1,30}){0,5}$")]
    private static partial Regex LanguageList();
}

/// <summary>
/// Processes a file version (DOC-04, DOC-07, DOC-08): extracts the text layer of PDFs; runs OCR for
/// images and PDFs without usable text and stores the result as a new, searchable PDF version (the
/// original stays); saves page texts for search, renders the thumbnail and re-indexes the item.
/// </summary>
internal sealed partial class DocumentProcessor(
    DocumentsDbContext db,
    IBlobStore blobs,
    FileIntake intake,
    OcrEngine ocr,
    PageRenderer renderer,
    IListItemStore items,
    ILiveEvents live,
    IUserPreferences preferences,
    ITenantContext tenant,
    ICurrentUser user,
    ILogger<DocumentProcessor> logger) : OperationHandler<ProcessFile>
{
    public const string OperationType = "documents.processFile";

    /// <summary>Below this many characters per page a PDF counts as a scan.</summary>
    private const int MinTextPerPage = 20;

    public override string Type => OperationType;

    protected override async Task<object?> ExecuteAsync(ProcessFile payload, IOperationProgress progress, CancellationToken ct)
    {
        var version = await db.FileVersions.FirstOrDefaultAsync(v => v.Id == payload.VersionId, ct);
        if (version is null)
        {
            return null; // The item was purged meanwhile.
        }

        await SetStatusAsync(version, ProcessingStatus.Running, null, ct);
        try
        {
            var result = await ProcessAsync(version, payload, progress, ct);
            await SetStatusAsync(version, ProcessingStatus.Succeeded, null, ct);
            return result;
        }
#pragma warning disable CA1031 // Any failure is recorded on the version; the operation fails too.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogProcessingFailed(ex, version.Id);
            db.ChangeTracker.Clear();
            var failed = await db.FileVersions.FirstAsync(v => v.Id == version.Id, ct);
            await SetStatusAsync(failed, ProcessingStatus.Failed, ex.Message, ct);
            throw;
        }
    }

    private async Task<object> ProcessAsync(FileVersion version, ProcessFile payload, IOperationProgress progress, CancellationToken ct)
    {
        var stored = await db.StoredFiles.FirstAsync(f => f.Id == version.StoredFileId, ct);
        var settings = await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(s => s.ListId == version.ListId, ct);
        // DOC-17: the request, the file, the library, then the uploader's (or the organization's) document languages.
        var languages = payload.Languages ?? version.Languages ?? settings?.OcrLanguages
            ?? (version.CreatedBy is { } uploader
                ? (await preferences.GetAsync(uploader, ct)).DocumentLanguages
                : (await preferences.GetDefaultsAsync(ct)).DocumentLanguages);
        var ocrMode = settings?.OcrMode ?? OcrMode.Auto;

        var work = Directory.CreateTempSubdirectory("pdn_process_");
        try
        {
            var source = Path.Combine(work.FullName, "source");
            await using (var content = await blobs.OpenReadAsync(stored.BlobKey, ct) ?? throw new InvalidOperationException("The stored content of the file is missing."))
            await using (var file = File.Create(source))
            {
                await content.CopyToAsync(file, ct);
            }

            List<string>? pages = null;
            if (version.MediaType == FileTypes.Pdf)
            {
                pages = TextExtractor.PdfPages(source);
                version.PageCount = pages.Count;
            }

            var needsOcr = payload.ForceOcr
                || (ocrMode == OcrMode.Auto && (pages is null || pages.Sum(p => p.Trim().Length) < MinTextPerPage * Math.Max(1, pages.Count)));
            await progress.ReportAsync(10, ct);

            var current = version;
            if (needsOcr)
            {
                var input = source;
                if (version.MediaType == FileTypes.Pdf)
                {
                    input = await renderer.RenderForOcrAsync(source, pages!.Count, work.FullName, ct);
                }

                var (pdf, text) = await ocr.RecognizeAsync(input, languages, Path.Combine(work.FullName, "ocr"), ct);
                await progress.ReportAsync(80, ct);
                current = await AddOcrVersionAsync(version, pdf, text, languages, ct) ?? version;
            }
            else
            {
                await SavePagesAsync(stored.Id, pages ?? [], ct);
                version.TextLanguage = FirstLanguage(languages);
                version.PageCount ??= 1;
            }

            await db.SaveChangesAsync(ct);
            await items.ReindexAsync(version.ItemId, ct);
            await renderer.WarmThumbnailAsync(current, ct);
            return new { current.Number, current.PageCount, Ocr = needsOcr };
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
        await SavePagesAsync(stored.Id, pages, ct);
        version.PageCount ??= pages.Count;
        version.TextLanguage = FirstLanguage(languages);
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
            ProcessingStatus = ProcessingStatus.Succeeded,
            PageCount = pages.Count,
            TextLanguage = FirstLanguage(languages),
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

    /// <summary>Stores page texts once per content (identical files share them).</summary>
    private async Task SavePagesAsync(Guid storedFileId, IReadOnlyList<string> pages, CancellationToken ct)
    {
        if (await db.Pages.AnyAsync(p => p.StoredFileId == storedFileId, ct))
        {
            return;
        }

        db.Pages.AddRange(pages.Select((text, i) => new StoredFilePage { StoredFileId = storedFileId, PageNumber = i + 1, Text = text.Trim() }));
        await db.SaveChangesAsync(ct);
    }

    private async Task SetStatusAsync(FileVersion version, ProcessingStatus status, string? error, CancellationToken ct)
    {
        version.ProcessingStatus = status;
        version.ProcessingError = error is null ? null : error.Length > 1000 ? error[..1000] : error;
        await db.SaveChangesAsync(ct);
        live.Publish(new LiveEvent("document.processing", tenant.TenantId!.Value, user.UserId, new
        {
            version.WorkspaceId,
            version.ListId,
            version.ItemId,
            Version = version.Number,
            Status = status,
            Error = version.ProcessingError,
        }));
    }

    private static string FirstLanguage(string languages) => languages.Split('+')[0];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing of file version {VersionId} failed.")]
    private partial void LogProcessingFailed(Exception exception, Guid versionId);
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
/// OCR with the Tesseract CLI (ADR-0015): one call reads all pages (an image, a multi-page TIFF or a
/// list of page images) and writes a searchable PDF (page image + invisible text) and the plain text.
/// </summary>
internal sealed class OcrEngine(IOptions<DocumentsOptions> options)
{
    public async Task<(string Pdf, IReadOnlyList<string> Pages)> RecognizeAsync(string input, string languages, string outputBase, CancellationToken ct)
    {
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
}

/// <summary>
/// Page images (DOC-04): renders PDF pages with PDFium (PDFtoImage) and resizes JPEG/PNG with
/// SkiaSharp. Results are cached in the blob store per content, page and width.
/// </summary>
internal sealed class PageRenderer(DocumentsDbContext db, IBlobStore blobs, IOptions<DocumentsOptions> options)
{
    /// <summary>Widths served (thumbnail, preview, large); requests are rounded up to one of them.</summary>
    public static readonly int[] Widths = [200, 800, 1600];

    // PDFium is not thread-safe.
    private static readonly SemaphoreSlim PdfiumLock = new(1, 1);

    /// <summary>PDFium ships for Linux, Windows and macOS (the container is Linux).</summary>
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("macos")]
    private static bool PdfiumSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static int WidthFor(int? requested) => Widths.FirstOrDefault(w => w >= (requested ?? Widths[0]), Widths[^1]);

    /// <summary>A JPEG of the page, or null when the page does not exist or the type cannot be rendered (TIFF before OCR).</summary>
    public async Task<Stream?> RenderAsync(FileVersion version, int page, int width, CancellationToken ct)
    {
        var stored = await db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == version.StoredFileId, ct);
        var key = $"{stored.TenantId:N}/renders/{stored.Sha256[..2]}/{stored.Sha256}/p{page}-w{width}";
        if (await blobs.OpenReadAsync(key, ct) is { } cached)
        {
            return cached;
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

        if (jpeg is null)
        {
            return null;
        }

        await blobs.WriteAsync(key, new MemoryStream(jpeg), ct);
        return new MemoryStream(jpeg);
    }

    /// <summary>Renders the thumbnail ahead of time so lists of documents load fast (best effort).</summary>
    public async Task WarmThumbnailAsync(FileVersion version, CancellationToken ct)
    {
        await using var _ = await RenderAsync(version, 1, Widths[0], ct);
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
