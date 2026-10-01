using System.Net;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

#pragma warning disable CA1416 // PDFium renders on Linux, Windows and macOS: where the tests run.

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// Documents composed from workflows (ADR-0038; DOC-04, DOC-07…09, API-07, SRC-05): an upload only stores the file; the
/// library's built-in workflows read the text, make thumbnails and page images, and (when turned on) OCR scans into a
/// searchable PDF version. OCR runs the real Tesseract CLI, like the container image.
/// </summary>
public sealed class DocumentProcessingTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await _host.SignInAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>A PDF with a text layer (standard font: no system fonts needed).</summary>
    internal static byte[] TextPdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < lines.Length; i++)
        {
            page.AddText(lines[i], 28, new PdfPoint(40, 760 - (i * 48)), font);
        }

        return builder.Build();
    }

    /// <summary>A "scan": the text PDF rendered to a PNG, so it has no text layer.</summary>
    private static byte[] ScanPng(params string[] lines)
    {
        using var output = new MemoryStream();
        PDFtoImage.Conversion.SavePng(output, TextPdf(lines), 0, password: null, new PDFtoImage.RenderOptions { Dpi = 200 });
        return output.ToArray();
    }

    /// <summary>A scanned PDF: a page that is only an image.</summary>
    private static byte[] ImagePdf(byte[] png)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.AddPng(png, new PdfRectangle(0, 0, 595, 842));
        return builder.Build();
    }

    private static async Task<(string Workspace, string Library)> LibraryAsync(HttpClient client, string name = "Documents", string? workspace = null)
    {
        var ws = workspace ?? await Api.CreateWorkspaceAsync(client, "Archive");
        using var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey = "documents" }, Ct);
        return (ws, (await response.JsonAsync(HttpStatusCode.Created)).Id());
    }

    private static async Task<string> UploadAsync(HttpClient client, string ws, string list, byte[] content, string fileName)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        using var response = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", form, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).GetProperty("itemId").GetString()!;
    }

    /// <summary>The library's document workflows, by key.</summary>
    private static async Task<Dictionary<string, JsonElement>> WorkflowsAsync(HttpClient client, string ws, string list) =>
        (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/workflows/builtIns", Ct)).JsonAsync(HttpStatusCode.OK))
            .EnumerateArray().ToDictionary(w => w.GetProperty("key").GetString()!);

    /// <summary>Turns a document workflow of the library on or off.</summary>
    private static async Task<JsonElement> SetAsync(HttpClient client, string ws, string list, string key, bool enabled, object? parameters = null)
    {
        var etag = (await WorkflowsAsync(client, ws, list))[key].TryGetProperty("@odata.etag", out var tag) ? tag.GetString() : null;
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/workflows/builtIns/{key}";
        using var response = etag is null
            ? await client.PutAsJsonAsync(url, new { enabled, parameters }, Ct)
            : await client.SendAsync(Api.WithETag(HttpMethod.Put, url, new { enabled, parameters }, etag), Ct);
        return await response.JsonAsync(HttpStatusCode.OK);
    }

    /// <summary>Waits until the item has at least <paramref name="count"/> workflow runs and all of them ended; returns them by built-in key.</summary>
    private static async Task<List<(string Key, JsonElement Run)>> RunsAsync(HttpClient client, string ws, string list, string item, int count)
    {
        var keys = (await WorkflowsAsync(client, ws, list)).Values
            .Where(w => w.GetProperty("workflowId").ValueKind == JsonValueKind.String)
            .ToDictionary(w => w.GetProperty("workflowId").GetString()!, w => w.GetProperty("key").GetString()!);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            var runs = (await (await client.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?itemId={item}", Ct)).JsonAsync(HttpStatusCode.OK))
                .GetProperty("value").EnumerateArray().ToList();
            if (runs.Count >= count && runs.All(r => r.GetProperty("status").GetString() is "completed" or "failed"))
            {
                return [.. runs.Select(r => (keys.GetValueOrDefault(r.GetProperty("workflowId").GetString()!, "?"), r))];
            }

            Assert.True(DateTime.UtcNow < deadline, string.Join(", ", runs.Select(r => $"{r.GetProperty("status")} {r.GetProperty("error")}")));
            await Task.Delay(200, Ct);
        }
    }

    private static async Task<List<JsonElement>> VersionsAsync(HttpClient client, string ws, string list, string item) =>
        [.. (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file/versions", Ct)).JsonAsync(HttpStatusCode.OK))
            .GetProperty("value").EnumerateArray()];

    private static async Task<List<string>> SearchAsync(HttpClient client, string query) =>
        [.. (await (await client.GetAsync($"/v1.0/search?q={Uri.EscapeDataString(query)}", Ct)).JsonAsync(HttpStatusCode.OK))
            .GetProperty("value").EnumerateArray().Select(h => h.GetProperty("id").GetString()!)];

    private static async Task FoundAsync(HttpClient client, string query, string item)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await SearchAsync(client, query)).Contains(item))
        {
            Assert.True(DateTime.UtcNow < deadline, $"'{query}' does not find {item}");
            await Task.Delay(200, Ct);
        }
    }

    [Fact]
    public async Task Uploads_only_store_the_file_and_the_library_workflows_do_the_rest()
    {
        var (ws, list) = await LibraryAsync(_admin);
        string File(string item) => $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";

        // A library has text, thumbnails and pages on, OCR off.
        var workflows = await WorkflowsAsync(_admin, ws, list);
        Assert.Equal(["documents.ocr", "documents.pages", "documents.text", "documents.thumbnail"], workflows.Keys.Order(StringComparer.Ordinal));
        Assert.True(workflows["documents.text"].GetProperty("enabled").GetBoolean());
        Assert.True(workflows["documents.thumbnail"].GetProperty("enabled").GetBoolean());
        Assert.True(workflows["documents.pages"].GetProperty("enabled").GetBoolean());
        Assert.False(workflows["documents.ocr"].GetProperty("enabled").GetBoolean());
        Assert.Equal("library", workflows["documents.text"].GetProperty("scope").GetString());

        var item = await UploadAsync(_admin, ws, list, TextPdf("Quarterly report", "Paperclips were ordered"), "report.pdf");
        var runs = await RunsAsync(_admin, ws, list, item, 3);
        Assert.Equal(["documents.pages", "documents.text", "documents.thumbnail"], runs.Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.All(runs, r => Assert.Equal("completed", r.Run.GetProperty("status").GetString()));

        var version = Assert.Single(await VersionsAsync(_admin, ws, list, item));
        Assert.Equal("upload", version.GetProperty("source").GetString());
        Assert.Equal(1, version.GetProperty("pageCount").GetInt32());
        Assert.Equal("eng", version.GetProperty("textLanguage").GetString());
        await FoundAsync(_admin, "paperclips", item);
        Assert.DoesNotContain(item, await SearchAsync(_admin, "invoice"));

        // DOC-04: what the workflows made is served as JPEG; a page they did not make is 404.
        using var thumbnail = await _admin.GetAsync($"{File(item)}/thumbnail", Ct);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType!.MediaType);
        Assert.Equal([0xFF, 0xD8], (await thumbnail.Content.ReadAsByteArrayAsync(Ct))[..2]);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"{File(item)}/pages/1/image?width=1600", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{File(item)}/pages/2/image", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_library_without_its_workflows_keeps_files_as_uploaded()
    {
        var (ws, plain) = await LibraryAsync(_admin, "Plain");
        var (_, full) = await LibraryAsync(_admin, "Full", ws);
        foreach (var key in new[] { "documents.text", "documents.thumbnail", "documents.pages" })
        {
            Assert.False((await SetAsync(_admin, ws, plain, key, enabled: false)).GetProperty("enabled").GetBoolean());
        }

        var stored = await UploadAsync(_admin, ws, plain, TextPdf("Unread walrus"), "stored.pdf");
        var read = await UploadAsync(_admin, ws, full, TextPdf("Read walrus"), "read.pdf");
        await RunsAsync(_admin, ws, full, read, 3);

        Assert.Empty(await RunsAsync(_admin, ws, plain, stored, 0));
        var file = $"/v1.0/workspaces/{ws}/lists/{plain}/items/{stored}/file";
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{file}/thumbnail", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"{file}/pages/1/image", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync(file, Ct)).StatusCode); // the file itself is there
        await FoundAsync(_admin, "walrus", read);
        Assert.DoesNotContain(stored, await SearchAsync(_admin, "walrus"));

        // Turned off stays off: the library's defaults are not created again.
        Assert.False((await WorkflowsAsync(_admin, ws, plain))["documents.text"].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Scans_are_ocred_when_the_library_turns_ocr_on()
    {
        var (ws, list) = await LibraryAsync(_admin);
        await SetAsync(_admin, ws, list, "documents.ocr", enabled: true);

        var item = await UploadAsync(_admin, ws, list, ScanPng("INVOICE 4711", "Stapler delivery"), "scan.png");

        // Read the text (no text) → Recognize text → the OCR version is read (has text) and gets its images: 7 runs.
        var runs = await RunsAsync(_admin, ws, list, item, 7);
        Assert.All(runs, r => Assert.True(r.Run.GetProperty("status").GetString() == "completed", r.Run.ToString()));
        var versions = await VersionsAsync(_admin, ws, list, item);
        Assert.Equal(["ocr", "upload"], versions.Select(v => v.GetProperty("source").GetString()));
        Assert.Equal("application/pdf", versions[0].GetProperty("mediaType").GetString());
        Assert.Equal("scan.pdf", versions[0].GetProperty("fileName").GetString());
        Assert.True(versions[0].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("image/png", versions[1].GetProperty("mediaType").GetString()); // the original stays

        // DOC-08: the current file is a PDF with a text layer.
        var file = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";
        var pdf = await _admin.GetByteArrayAsync(file, Ct);
        using (var document = PdfDocument.Open(pdf))
        {
            Assert.Contains("4711", document.GetPage(1).Text, StringComparison.Ordinal);
        }

        await FoundAsync(_admin, "4711", item);
        Assert.Contains(item, await SearchAsync(_admin, "stapler"));
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"{file}/pages/1/image", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"{file}/pages/1/image?version=1", Ct)).StatusCode); // the original's page
    }

    [Fact]
    public async Task Ocr_is_run_again_by_hand_and_its_failures_fail_the_run()
    {
        var (ws, list) = await LibraryAsync(_admin);
        var ocrWorkflow = (await SetAsync(_admin, ws, list, "documents.ocr", enabled: true)).GetProperty("workflowId").GetString();

        // A scanned PDF (an image only) is recognized.
        var scanned = await UploadAsync(_admin, ws, list, ImagePdf(ScanPng("Receipt 9182")), "receipt.pdf");
        await RunsAsync(_admin, ws, list, scanned, 7);
        Assert.Equal(["ocr", "upload"], (await VersionsAsync(_admin, ws, list, scanned)).Select(v => v.GetProperty("source").GetString()));

        // A PDF with text is not, until someone starts OCR by hand with force.
        var text = await UploadAsync(_admin, ws, list, TextPdf("Contract between two parties"), "contract.pdf");
        await RunsAsync(_admin, ws, list, text, 3);
        Assert.Single(await VersionsAsync(_admin, ws, list, text));
        using (var start = await _admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/workflows/{ocrWorkflow}/runs",
            new { listId = list, itemId = text, inputs = new { force = true } }, Ct))
        {
            await start.JsonAsync(HttpStatusCode.Accepted);
        }

        await RunsAsync(_admin, ws, list, text, 7);
        Assert.Equal("ocr", (await VersionsAsync(_admin, ws, list, text))[0].GetProperty("source").GetString());

        // A language Tesseract does not have: the OCR run fails with the error.
        await SetAsync(_admin, ws, list, "documents.ocr", enabled: true, new { languages = "xyz" });
        var failing = await UploadAsync(_admin, ws, list, ScanPng("Manual"), "manual.png");
        var runs = await RunsAsync(_admin, ws, list, failing, 4);
        var ocr = Assert.Single(runs, r => r.Key == "documents.ocr").Run;
        Assert.Equal("failed", ocr.GetProperty("status").GetString());
        Assert.Contains("OCR failed", ocr.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Single(await VersionsAsync(_admin, ws, list, failing));
    }

    [Fact]
    public async Task Live_events_report_what_the_workflows_made()
    {
        var (ws, list) = await LibraryAsync(_admin);
        using var stream = await _admin.GetAsync("/v1.0/me/events", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(Ct));
        Assert.Equal("event: connected", await reader.ReadLineAsync(Ct));

        var item = await UploadAsync(_admin, ws, list, TextPdf("Live"), "live.pdf");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var seen = new HashSet<string>();
        string? eventType = null;
        while (seen.Count < 3 && await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventType == "document.changed")
            {
                var data = JsonElement.Parse(line[6..]);
                if (data.GetProperty("itemId").GetString() == item)
                {
                    seen.Add(data.GetProperty("what").GetString()!);
                }
            }
        }

        Assert.Equal(["pages", "text", "thumbnail"], seen.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Document_workflows_and_files_respect_tenants()
    {
        var (ws, list) = await LibraryAsync(_admin);
        var item = await UploadAsync(_admin, ws, list, TextPdf("Private"), "private.pdf");
        await RunsAsync(_admin, ws, list, item, 3);
        var file = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";

        var other = await _host.CreateTenantAsync("processing-other");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{file}/thumbnail", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{file}/pages/1/image", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/workspaces/{ws}/workflows/runs?itemId={item}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/workflows/builtIns", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/workflows/builtIns/documents.ocr", new { enabled = true }, Ct)).StatusCode);
        Assert.DoesNotContain(item, await SearchAsync(other, "private"));
    }
}
