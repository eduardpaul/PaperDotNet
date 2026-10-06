using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>What the document activities share: the run item's current file.</summary>
internal static class DocumentActivity
{
    public const string NoFile = "The item has no file (document steps work on documents of a library).";

    public static async Task<FileVersion?> CurrentAsync(DocumentsDbContext db, WorkflowActivityContext context, CancellationToken ct) =>
        context.Item is { } item ? await db.FileVersions.FirstOrDefaultAsync(v => v.ItemId == item.ItemId && v.IsCurrent, ct) : null;

    /// <summary>Runs work on the file; a file that cannot be read fails the step with why (it would fail again on a retry).</summary>
    public static async Task<WorkflowActivityResult> ReadingAsync(Func<Task<WorkflowActivityResult>> work)
    {
        try
        {
            return await work();
        }
#pragma warning disable CA1031 // A broken or unsupported file is the step's failure, not the server's.
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
#pragma warning restore CA1031
        {
            return WorkflowActivityResult.Fail($"The file cannot be read: {ex.Message}");
        }
    }

    public static JsonObject Output(FileVersion version, params (string Name, JsonNode? Value)[] more)
    {
        var output = new JsonObject { ["version"] = version.Number, ["pageCount"] = version.PageCount };
        foreach (var (name, value) in more)
        {
            output[name] = value;
        }

        return output;
    }
}

/// <summary>
/// <c>document.readText</c> (ADR-0038): the text layer of a PDF as page texts, the page count and the search text of the
/// item. Continues on <c>text</c> when the file has text, on <c>noText</c> for scans and photos (e.g. to OCR them).
/// </summary>
internal sealed class ReadTextActivity(
    DocumentsDbContext db, IBlobStore blobs, IUserPreferences preferences, ILiveEvents live, ITenantContext tenant, ICurrentUser user)
    : IWorkflowActivity
{
    public string Key => "document.readText";

    public string Description => "Reads the text layer of a PDF into page texts for search; continues on text, or noText for scans and photos.";

    public IReadOnlyList<string> Outcomes => ["text", "noText"];

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("version", ActivitySchemas.Number("The file version read.")), ("pageCount", ActivitySchemas.Number("Its pages.")),
        ("hasText", ActivitySchemas.Boolean("Whether it has text.")));

    public Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken) =>
        DocumentActivity.ReadingAsync(() => ReadAsync(context, cancellationToken));

    private async Task<WorkflowActivityResult> ReadAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (await DocumentActivity.CurrentAsync(db, context, cancellationToken) is not { } version)
        {
            return WorkflowActivityResult.Fail(DocumentActivity.NoFile);
        }

        var pages = await DocumentText.PagesAsync(db, version.StoredFileId, cancellationToken);
        if (pages.Count == 0 && version.MediaType == FileTypes.Pdf)
        {
            var stored = await db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == version.StoredFileId, cancellationToken);
            var work = Directory.CreateTempSubdirectory("pdn_text_");
            try
            {
                var source = Path.Combine(work.FullName, "source");
                await using (var content = await blobs.OpenReadAsync(stored.BlobKey, cancellationToken)
                    ?? throw new InvalidOperationException("The stored content of the file is missing."))
                await using (var file = File.Create(source))
                {
                    await content.CopyToAsync(file, cancellationToken);
                }

                pages = TextExtractor.PdfPages(source);
            }
            finally
            {
                work.Delete(recursive: true);
            }

            await DocumentText.SavePagesAsync(db, version.StoredFileId, pages, cancellationToken);
        }

        version.PageCount ??= Math.Max(1, pages.Count);
        version.TextLanguage ??= DocumentText.FirstLanguage(await DocumentText.LanguagesAsync(db, preferences, version, null, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        live.Publish(DocumentLiveEvents.Changed(tenant, user, version, "text"));

        var hasText = version.Source == "ocr" || DocumentText.Enough(pages);
        return WorkflowActivityResult.Ok(hasText ? "text" : "noText", DocumentActivity.Output(version, ("hasText", hasText)));
    }
}

/// <summary><c>document.thumbnail</c> (ADR-0038): the thumbnail of the first page. Without it a library shows no thumbnails.</summary>
internal sealed class ThumbnailActivity(DocumentsDbContext db, PageRenderer renderer, ILiveEvents live, ITenantContext tenant, ICurrentUser user)
    : IWorkflowActivity
{
    public string Key => "document.thumbnail";

    public string Description => "Makes the thumbnail of a document's first page.";

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("version", ActivitySchemas.Number("The file version.")), ("thumbnail", ActivitySchemas.Boolean("Whether one could be made (not for TIFF before OCR).")));

    public Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken) =>
        DocumentActivity.ReadingAsync(() => MakeAsync(context, cancellationToken));

    private async Task<WorkflowActivityResult> MakeAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (await DocumentActivity.CurrentAsync(db, context, cancellationToken) is not { } version)
        {
            return WorkflowActivityResult.Fail(DocumentActivity.NoFile);
        }

        var made = await renderer.RenderAsync(version, 1, PageRenderer.Widths[0], store: true, cancellationToken) is not null;
        live.Publish(DocumentLiveEvents.Changed(tenant, user, version, "thumbnail"));
        return WorkflowActivityResult.Ok(DocumentActivity.Output(version, ("thumbnail", made)));
    }
}

/// <summary>
/// <c>document.renderPages</c> (ADR-0038): every page as an image at the preview and large widths, for viewing. Without it
/// a library shows no page previews.
/// </summary>
internal sealed class RenderPagesActivity(
    DocumentsDbContext db, PageRenderer renderer, Microsoft.Extensions.Options.IOptions<DocumentsOptions> options, ILiveEvents live, ITenantContext tenant,
    ICurrentUser user) : IWorkflowActivity
{
    public string Key => "document.renderPages";

    public string Description => "Renders every page of a document as images for viewing (preview and large sizes).";

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("version", ActivitySchemas.Number("The file version.")), ("pages", ActivitySchemas.Number("Pages rendered.")));

    public Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken) =>
        DocumentActivity.ReadingAsync(() => RenderAsync(context, cancellationToken));

    private async Task<WorkflowActivityResult> RenderAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (await DocumentActivity.CurrentAsync(db, context, cancellationToken) is not { } version)
        {
            return WorkflowActivityResult.Fail(DocumentActivity.NoFile);
        }

        var rendered = 0;
        for (var page = 1; page <= options.Value.MaxRenderedPages; page++)
        {
            var any = false;
            foreach (var width in PageRenderer.PageWidths)
            {
                any |= await renderer.RenderAsync(version, page, width, store: true, cancellationToken) is not null;
            }

            if (!any)
            {
                break;
            }

            rendered = page;
        }

        live.Publish(DocumentLiveEvents.Changed(tenant, user, version, "pages"));
        return WorkflowActivityResult.Ok(DocumentActivity.Output(version, ("pages", rendered)));
    }
}

/// <summary>
/// <c>document.ocr</c> (DOC-07, ADR-0038): recognizes the text of a scan or photo as a new PDF version with page texts. It
/// starts the OCR operation and waits for it (the run holds no server meanwhile). A file that has text is left as it is,
/// unless <c>force</c>. <c>languages</c> (e.g. <c>deu+eng</c>) default to the file's, the library's, then the uploader's.
/// </summary>
internal sealed class OcrActivity(DocumentsDbContext db, IOperations operations, TimeProvider time) : IWorkflowActivity
{
    /// <summary>How long a step waits for OCR before it fails.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);

    public string Key => "document.ocr";

    public string Description => "Recognizes the text of scans and photos (OCR) as a new, searchable PDF version; files with text are left as they are.";

    public IEnumerable<string> Validate(JsonObject inputs) =>
        ActivityInputs.Text(inputs, "languages") is { } languages && !languages.Contains('{', StringComparison.Ordinal) && !DocumentText.IsValidLanguageList(languages)
            ? ["languages are Tesseract codes joined with +, e.g. deu+eng."]
            : [];

    public JsonObject? InputSchema => ActivitySchemas.Of([],
        ("languages", ActivitySchemas.Text("Tesseract languages, e.g. deu+eng (default: the file's, the library's, the uploader's).")),
        ("force", ActivitySchemas.Any("true to recognize the text even when the file has text (a token such as {var:force} works too).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("version", ActivitySchemas.Number("The current file version (the OCR result when it ran).")), ("pageCount", ActivitySchemas.Number("Its pages.")),
        ("ocr", ActivitySchemas.Boolean("Whether OCR ran.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Resumed is { Kind: DocumentOcr.WaitKind } resumed)
        {
            if (resumed.Payload?["error"] is JsonValue error)
            {
                return WorkflowActivityResult.Fail($"OCR failed: {error}");
            }

            if (resumed.TimedOut)
            {
                return WorkflowActivityResult.Fail($"OCR did not finish within {MaxWait.TotalHours:0} hours.");
            }

            var done = await DocumentActivity.CurrentAsync(db, context, cancellationToken);
            return done is null
                ? WorkflowActivityResult.Fail(DocumentActivity.NoFile)
                : WorkflowActivityResult.Ok(DocumentActivity.Output(done, ("ocr", resumed.Payload?["ocr"]?.DeepClone() ?? false)));
        }

        if (await DocumentActivity.CurrentAsync(db, context, cancellationToken) is not { } version)
        {
            return WorkflowActivityResult.Fail(DocumentActivity.NoFile);
        }

        var force = context.Inputs["force"] switch
        {
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            JsonValue value when value.TryGetValue<string>(out var text) => bool.TryParse(await context.ExpandAsync(text, cancellationToken), out var parsed) && parsed,
            _ => false,
        };
        if (!force && await DocumentText.HasTextAsync(db, version, cancellationToken))
        {
            return WorkflowActivityResult.Ok(DocumentActivity.Output(version, ("ocr", false)));
        }

        var languages = ActivityInputs.Text(context.Inputs, "languages") is { } template ? await context.ExpandAsync(template, cancellationToken) : null;
        if (string.IsNullOrWhiteSpace(languages))
        {
            languages = null;
        }
        else if (!DocumentText.IsValidLanguageList(languages))
        {
            return WorkflowActivityResult.Fail($"'{languages}' are not Tesseract languages (e.g. deu+eng).");
        }

        // The operation completes this wait when it ends (a completion that comes first is kept for it).
        var key = context.ExecutionId.ToString("N");
        await operations.StartAsync(DocumentOcr.OperationType, new OcrFile(version.Id, force, languages, key), cancellationToken);
        return WorkflowActivityResult.WaitAndRunAgain(DocumentOcr.WaitKind, key, time.GetUtcNow() + MaxWait, new JsonObject { ["version"] = version.Id.ToString() });
    }
}
