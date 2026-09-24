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
/// Documents, slice 3b: text extraction, OCR into a searchable PDF version, page images, processing
/// status, live events and language-aware search (DOC-04, DOC-07…09, API-07, SRC-05). OCR runs the
/// real Tesseract CLI, like the container image.
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

    private static async Task<(Guid Workspace, Guid Library)> LibraryAsync(HttpClient client)
    {
        var ws = await client.CreateWorkspaceAsync("Archive");
        var response = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Documents", templateKey = "documents" }, Ct);
        return (ws, (await response.ReadJsonAsync()).GetProperty("id").GetGuid());
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid ws, Guid list, byte[] content, string fileName)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        var response = await client.PostAsync($"/v1.0/workspaces/{ws}/lists/{list}/documents", form, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("itemId").GetGuid();
    }

    /// <summary>Waits until the item's current file is processed; returns the versions, newest first.</summary>
    private static async Task<List<JsonElement>> ProcessedAsync(HttpClient client, Guid ws, Guid list, Guid item)
    {
        List<JsonElement> versions = [];
        await Eventually.WaitForAsync<bool>(async () =>
        {
            var body = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file/versions", Ct)).ReadJsonAsync();
            versions = body.GetProperty("value").EnumerateArray().ToList();
            return versions.All(v => v.GetProperty("processingStatus").GetString() is "succeeded" or "failed") ? true : null;
        }, TimeSpan.FromSeconds(90));
        return versions;
    }

    private static async Task<List<Guid>> SearchAsync(HttpClient client, string query)
    {
        var result = await (await client.GetAsync($"/v1.0/search?q={Uri.EscapeDataString(query)}", Ct)).ReadJsonAsync();
        return result.GetProperty("value").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task Pdfs_with_text_are_indexed_without_ocr_and_stemmed()
    {
        await factory.CreateTenantAsync("proc-text");
        var client = await ApiClient.CreateAsync(factory, "proc-text");
        var (ws, list) = await LibraryAsync(client);

        var item = await UploadAsync(client, ws, list, TextPdf("Quarterly report", "Paperclips were ordered"), "report.pdf");
        var version = Assert.Single(await ProcessedAsync(client, ws, list, item));

        Assert.Equal("succeeded", version.GetProperty("processingStatus").GetString());
        Assert.Equal("upload", version.GetProperty("source").GetString());
        Assert.Equal(1, version.GetProperty("pageCount").GetInt32());
        Assert.Equal("eng", version.GetProperty("textLanguage").GetString());
        await Eventually.WaitForAsync<bool>(async () => (await SearchAsync(client, "paperclips")).Contains(item) ? true : null);

        // SRC-05: English stemming ("order" finds "ordered") on both providers.
        Assert.Contains(item, await SearchAsync(client, "order"));
        Assert.DoesNotContain(item, await SearchAsync(client, "invoice"));
    }

    [Fact]
    public async Task Scans_are_ocred_into_a_searchable_pdf_version()
    {
        await factory.CreateTenantAsync("proc-ocr");
        var client = await ApiClient.CreateAsync(factory, "proc-ocr");
        var (ws, list) = await LibraryAsync(client);
        var scan = ScanPng("INVOICE 4711", "Stapler delivery");

        var item = await UploadAsync(client, ws, list, scan, "scan.png");
        var versions = await ProcessedAsync(client, ws, list, item);

        Assert.Equal(["ocr", "upload"], versions.Select(v => v.GetProperty("source").GetString()));
        Assert.Equal("application/pdf", versions[0].GetProperty("mediaType").GetString());
        Assert.Equal("scan.pdf", versions[0].GetProperty("fileName").GetString());
        Assert.True(versions[0].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("image/png", versions[1].GetProperty("mediaType").GetString()); // the original stays

        // DOC-08: the current file is a PDF with a text layer.
        var pdf = await client.GetByteArrayAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file", Ct);
        using (var document = PdfDocument.Open(pdf))
        {
            Assert.Contains("4711", document.GetPage(1).Text, StringComparison.Ordinal);
        }

        await Eventually.WaitForAsync<bool>(async () => (await SearchAsync(client, "4711")).Contains(item) ? true : null);
        Assert.Contains(item, await SearchAsync(client, "stapler"));

        // DOC-04: thumbnails and page images as JPEG; a missing page is 404.
        var thumbnail = await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file/thumbnail", Ct);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType!.MediaType);
        Assert.Equal([0xFF, 0xD8], (await thumbnail.Content.ReadAsByteArrayAsync(Ct))[..2]);
        var original = await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file/pages/1/image?width=500&version=1", Ct);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file/pages/2/image", Ct)).StatusCode);
    }

    [Fact]
    public async Task Scanned_pdfs_are_ocred_and_ocr_can_be_forced()
    {
        await factory.CreateTenantAsync("proc-scanpdf");
        var client = await ApiClient.CreateAsync(factory, "proc-scanpdf");
        var (ws, list) = await LibraryAsync(client);

        var scanned = await UploadAsync(client, ws, list, ImagePdf(ScanPng("Receipt 9182")), "receipt.pdf");
        var versions = await ProcessedAsync(client, ws, list, scanned);
        Assert.Equal(["ocr", "upload"], versions.Select(v => v.GetProperty("source").GetString()));

        var text = await UploadAsync(client, ws, list, TextPdf("Contract between two parties"), "contract.pdf");
        Assert.Single(await ProcessedAsync(client, ws, list, text));
        var process = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{text}/file/process", new { forceOcr = true }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);
        var operation = process.Headers.Location!.ToString();
        await Eventually.WaitForAsync<bool>(async () =>
            (await (await client.GetAsync(operation, Ct)).ReadJsonAsync()).GetProperty("status").GetString() == "succeeded" ? true : null,
            TimeSpan.FromSeconds(90));
        Assert.Equal("ocr", (await ProcessedAsync(client, ws, list, text))[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Processing_follows_the_library_settings_and_reports_failures()
    {
        await factory.CreateTenantAsync("proc-settings");
        var client = await ApiClient.CreateAsync(factory, "proc-settings");
        var (ws, list) = await LibraryAsync(client);
        var settingsUrl = $"/v1.0/workspaces/{ws}/lists/{list}/documentSettings";

        var invalid = await client.PutAsJsonAsync(settingsUrl, new { ocrLanguages = "../etc" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var saved = await (await client.PutAsJsonAsync(settingsUrl, new { autoProcess = false }, Ct)).ReadJsonAsync();
        Assert.False(saved.GetProperty("autoProcess").GetBoolean());
        Assert.Equal("eng", saved.GetProperty("ocrLanguages").GetString());

        var item = await UploadAsync(client, ws, list, ScanPng("Manual"), "manual.png");
        var versions = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file/versions", Ct)).ReadJsonAsync();
        Assert.Equal("none", versions.GetProperty("value")[0].GetProperty("processingStatus").GetString());

        // A language Tesseract does not have: the version and the operation report the failure.
        var process = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file/process", new { languages = "xyz" }, Ct);
        Assert.Equal(HttpStatusCode.Accepted, process.StatusCode);
        var failed = Assert.Single(await ProcessedAsync(client, ws, list, item));
        Assert.Equal("failed", failed.GetProperty("processingStatus").GetString());
        Assert.False(string.IsNullOrEmpty(failed.GetProperty("processingError").GetString()));
        var operation = await (await client.GetAsync(process.Headers.Location, Ct)).ReadJsonAsync();
        Assert.Equal("failed", operation.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Live_events_report_processing()
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
        var seen = new List<string>();
        string? eventType = null;
        while (!seen.Contains("document.processing:succeeded") && await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventType is not null)
            {
                var data = JsonDocument.Parse(line[6..]).RootElement;
                if (eventType == "document.processing" && data.GetProperty("itemId").GetGuid() == item)
                {
                    seen.Add($"{eventType}:{data.GetProperty("status").GetString()}");
                }
                else if (eventType == "operation")
                {
                    seen.Add($"operation:{data.GetProperty("status").GetString()}");
                }
            }
        }

        Assert.Contains("document.processing:running", seen);
        Assert.Contains("document.processing:succeeded", seen);
        Assert.Contains("operation:running", seen);
    }

    [Fact]
    public async Task Processing_endpoints_respect_tenants()
    {
        await factory.CreateTenantAsync("proc-iso-a");
        await factory.CreateTenantAsync("proc-iso-b");
        var a = await ApiClient.CreateAsync(factory, "proc-iso-a");
        var b = await ApiClient.CreateAsync(factory, "proc-iso-b");
        var (ws, list) = await LibraryAsync(a);
        var item = await UploadAsync(a, ws, list, TextPdf("Private"), "private.pdf");
        await ProcessedAsync(a, ws, list, item);
        var file = $"/v1.0/workspaces/{ws}/lists/{list}/items/{item}/file";

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{file}/thumbnail", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"{file}/process", new { }, Ct)).StatusCode);
        Assert.DoesNotContain(item, await SearchAsync(b, "private"));
    }
}
