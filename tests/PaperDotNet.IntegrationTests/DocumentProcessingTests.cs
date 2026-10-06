using System.Net;
using System.Net.Http.Json;
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
public sealed class DocumentProcessingTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A PDF with a text layer (standard font: no system fonts needed).</summary>
    private static byte[] TextPdf(params string[] lines)
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

    private static async Task<(Guid Workspace, Guid Library)> LibraryAsync(HttpClient client, string name = "Documents", Guid? workspace = null)
    {
        var ws = workspace ?? await client.CreateWorkspaceAsync("Archive");
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name, templateKey = "documents" }, Ct);
        return (ws, (await response.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid ws, Guid list, byte[] content, string fileName)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        var response = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", form, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("itemId").GetGuid();
    }

    /// <summary>The library's document workflows, by key.</summary>
    private static async Task<Dictionary<string, JsonElement>> WorkflowsAsync(HttpClient client, Guid ws, Guid list) =>
        (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/workflows/builtIns", Ct)).ReadJsonAsync())
            .EnumerateArray().ToDictionary(w => w.GetProperty("key").GetString()!);

    /// <summary>Turns a document workflow of the library on or off.</summary>
    private static async Task<JsonElement> SetAsync(HttpClient client, Guid ws, Guid list, string key, bool enabled, object? parameters = null)
    {
        var etag = (await WorkflowsAsync(client, ws, list))[key].TryGetProperty("@odata.etag", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
        var url = $"/v1.0/workspaces/{ws}/lists/{list}/workflows/builtIns/{key}";
        var response = etag is null
            ? await client.PutAsJsonAsync(url, new { enabled, parameters }, Ct)
            : await client.SendWithEtagAsync(HttpMethod.Put, url, etag, new { enabled, parameters });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadJsonAsync();
    }

    private static Task<List<JsonElement>> RunsAsync(HttpClient client, Guid ws, Guid item, int count) => DocumentWorkflowRuns.WaitAsync(client, ws, item, count);

    private static async Task<List<JsonElement>> VersionsAsync(HttpClient client, Guid ws, Guid list, Guid item) =>
        (await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file/versions", Ct)).ReadJsonAsync())
            .GetProperty("value").EnumerateArray().ToList();

    private static async Task<List<Guid>> SearchAsync(HttpClient client, string query)
    {
        var result = await (await client.GetAsync($"/v1.0/search?q={Uri.EscapeDataString(query)}", Ct)).ReadJsonAsync();
        return result.GetProperty("value").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task Uploads_only_store_the_file_and_the_library_workflows_do_the_rest()
    {
        await factory.CreateTenantAsync("proc-text");
        var client = await ApiClient.CreateAsync(factory, "proc-text");
        var (ws, list) = await LibraryAsync(client);
        var file = (Guid item) => $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";

        // A library has text, thumbnails and pages on, OCR off.
        var workflows = await WorkflowsAsync(client, ws, list);
        Assert.Equal(["documents.ocr", "documents.pages", "documents.text", "documents.thumbnail", "search.index"], workflows.Keys.Order(StringComparer.Ordinal));
        Assert.True(workflows["documents.text"].GetProperty("enabled").GetBoolean());
        Assert.True(workflows["documents.thumbnail"].GetProperty("enabled").GetBoolean());
        Assert.True(workflows["documents.pages"].GetProperty("enabled").GetBoolean());
        Assert.False(workflows["documents.ocr"].GetProperty("enabled").GetBoolean());
        Assert.Equal("library", workflows["documents.text"].GetProperty("scope").GetString());

        var item = await UploadAsync(client, ws, list, TextPdf("Quarterly report", "Paperclips were ordered"), "report.pdf");
        var runs = await RunsAsync(client, ws, item, 3);
        Assert.Equal(["Make thumbnails (Documents)", "Read the text (Documents)", "Render pages (Documents)"],
            runs.Select(r => r.GetProperty("workflow").GetString()).Order(StringComparer.Ordinal));
        Assert.All(runs, r => Assert.Equal("completed", r.GetProperty("status").GetString()));

        var version = Assert.Single(await VersionsAsync(client, ws, list, item));
        Assert.Equal("upload", version.GetProperty("source").GetString());
        Assert.Equal(1, version.GetProperty("pageCount").GetInt32());
        Assert.Equal("eng", version.GetProperty("textLanguage").GetString());
        await Eventually.WaitForAsync<bool>(async () => (await SearchAsync(client, "paperclips")).Contains(item) ? true : null);

        // SRC-05: English stemming ("order" finds "ordered") on both providers.
        Assert.Contains(item, await SearchAsync(client, "order"));
        Assert.DoesNotContain(item, await SearchAsync(client, "invoice"));

        // DOC-04: what the workflows made is served as JPEG; a page they did not make is 404.
        var thumbnail = await client.GetAsync($"{file(item)}/thumbnail", Ct);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType!.MediaType);
        Assert.Equal([0xFF, 0xD8], (await thumbnail.Content.ReadAsByteArrayAsync(Ct))[..2]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{file(item)}/pages/1/image?width=1600", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{file(item)}/pages/2/image", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_library_without_its_workflows_keeps_files_as_uploaded()
    {
        await factory.CreateTenantAsync("proc-off");
        var client = await ApiClient.CreateAsync(factory, "proc-off");
        var (ws, plain) = await LibraryAsync(client, "Plain");
        var (_, full) = await LibraryAsync(client, "Full", ws);
        foreach (var key in new[] { "documents.text", "documents.thumbnail", "documents.pages" })
        {
            Assert.False((await SetAsync(client, ws, plain, key, enabled: false)).GetProperty("enabled").GetBoolean());
        }

        var stored = await UploadAsync(client, ws, plain, TextPdf("Unread walrus"), "stored.pdf");
        var read = await UploadAsync(client, ws, full, TextPdf("Read walrus"), "read.pdf");
        await RunsAsync(client, ws, read, 3);

        Assert.Empty(await RunsAsync(client, ws, stored, 0));
        var file = $"/v1.0/workspaces/{ws}/lists/{plain}/items/{stored}/file";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{file}/thumbnail", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{file}/pages/1/image", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(file, Ct)).StatusCode); // the file itself is there
        await Eventually.WaitForAsync<bool>(async () => (await SearchAsync(client, "walrus")).Contains(read) ? true : null);
        Assert.DoesNotContain(stored, await SearchAsync(client, "walrus"));

        // Turned off stays off: the library's defaults are not created again.
        Assert.False((await WorkflowsAsync(client, ws, plain))["documents.text"].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Scans_are_ocred_when_the_library_turns_ocr_on()
    {
        await factory.CreateTenantAsync("proc-ocr");
        var client = await ApiClient.CreateAsync(factory, "proc-ocr");
        var (ws, list) = await LibraryAsync(client);
        await SetAsync(client, ws, list, "documents.ocr", enabled: true);

        var item = await UploadAsync(client, ws, list, ScanPng("INVOICE 4711", "Stapler delivery"), "scan.png");

        // Read the text (no text) → Recognize text → the OCR version is read (has text) and gets its images: 7 runs.
        var runs = await RunsAsync(client, ws, item, 7);
        Assert.All(runs, r => Assert.Equal("completed", r.GetProperty("status").GetString()));
        var versions = await VersionsAsync(client, ws, list, item);
        Assert.Equal(["ocr", "upload"], versions.Select(v => v.GetProperty("source").GetString()));
        Assert.Equal("application/pdf", versions[0].GetProperty("mediaType").GetString());
        Assert.Equal("scan.pdf", versions[0].GetProperty("fileName").GetString());
        Assert.True(versions[0].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("image/png", versions[1].GetProperty("mediaType").GetString()); // the original stays

        // DOC-08: the current file is a PDF with a text layer.
        var file = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";
        var pdf = await client.GetByteArrayAsync(file, Ct);
        using (var document = PdfDocument.Open(pdf))
        {
            Assert.Contains("4711", document.GetPage(1).Text, StringComparison.Ordinal);
        }

        await Eventually.WaitForAsync<bool>(async () => (await SearchAsync(client, "4711")).Contains(item) ? true : null);
        Assert.Contains(item, await SearchAsync(client, "stapler"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{file}/pages/1/image", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{file}/pages/1/image?version=1", Ct)).StatusCode); // the original's page
    }

    [Fact]
    public async Task Ocr_is_run_again_by_hand_and_its_failures_fail_the_run()
    {
        await factory.CreateTenantAsync("proc-scanpdf");
        var client = await ApiClient.CreateAsync(factory, "proc-scanpdf");
        var (ws, list) = await LibraryAsync(client);
        await SetAsync(client, ws, list, "documents.ocr", enabled: true);

        // A scanned PDF (an image only) is recognized.
        var scanned = await UploadAsync(client, ws, list, ImagePdf(ScanPng("Receipt 9182")), "receipt.pdf");
        await RunsAsync(client, ws, scanned, 7);
        Assert.Equal(["ocr", "upload"], (await VersionsAsync(client, ws, list, scanned)).Select(v => v.GetProperty("source").GetString()));

        // A PDF with text is not, until someone starts OCR by hand with force.
        var text = await UploadAsync(client, ws, list, TextPdf("Contract between two parties"), "contract.pdf");
        await RunsAsync(client, ws, text, 3);
        Assert.Single(await VersionsAsync(client, ws, list, text));
        var start = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{text}/workflows",
            new { workflow = "Recognize text (Documents)", inputs = new { force = true } }, Ct);
        Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync(Ct));
        await RunsAsync(client, ws, text, 7);
        Assert.Equal("ocr", (await VersionsAsync(client, ws, list, text))[0].GetProperty("source").GetString());

        // A language Tesseract does not have: the OCR run fails with the error.
        await SetAsync(client, ws, list, "documents.ocr", enabled: true, new { languages = "xyz" });
        var failing = await UploadAsync(client, ws, list, ScanPng("Manual"), "manual.png");
        var runs = await RunsAsync(client, ws, failing, 4);
        var ocr = Assert.Single(runs, r => r.GetProperty("workflow").GetString() == "Recognize text (Documents)");
        Assert.Equal("failed", ocr.GetProperty("status").GetString());
        Assert.Contains("OCR failed", ocr.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Single(await VersionsAsync(client, ws, list, failing));

        // Settings: OCR languages are checked.
        var invalid = await client.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/documentSettings", new { ocrLanguages = "../etc" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Live_events_report_what_the_workflows_made()
    {
        await factory.CreateTenantAsync("proc-live");
        var client = await ApiClient.CreateAsync(factory, "proc-live");
        var (ws, list) = await LibraryAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me/events");
        using var stream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(Ct));
        Assert.Equal("event: connected", await reader.ReadLineAsync(Ct));

        var item = await UploadAsync(client, ws, list, TextPdf("Live"), "live.pdf");
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
                var data = JsonDocument.Parse(line[6..]).RootElement;
                if (data.GetProperty("itemId").GetGuid() == item)
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
        await factory.CreateTenantAsync("proc-iso-a");
        await factory.CreateTenantAsync("proc-iso-b");
        var a = await ApiClient.CreateAsync(factory, "proc-iso-a");
        var b = await ApiClient.CreateAsync(factory, "proc-iso-b");
        var (ws, list) = await LibraryAsync(a);
        var item = await UploadAsync(a, ws, list, TextPdf("Private"), "private.pdf");
        await RunsAsync(a, ws, item, 3);
        var file = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{file}/thumbnail", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/workflows/builtIns", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await b.PutAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/workflows/builtIns/documents.ocr", new { enabled = true }, Ct)).StatusCode);
        Assert.DoesNotContain(item, await SearchAsync(b, "private"));
    }
}
