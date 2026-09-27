using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PaperDotNet.Documents.Features;
using SkiaSharp;
using UglyToad.PdfPig;

namespace PaperDotNet.UnitTests;

public sealed class GlmOcrTests
{
    [Fact]
    public async Task Glm_reads_each_page_and_stores_a_searchable_pdf()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pdn_glm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var first = Png(directory, "a.png");
            var second = Png(directory, "b.png");
            var list = Path.Combine(directory, "pages.txt");
            await File.WriteAllLinesAsync(list, [first, second], ct);
            var script = new ScriptedOcr("INVOICE 4711", "SALMÓN 2,99 €");
            var engine = Engine(script, new DocumentsOptions { Engine = "glm", GlmModel = "glm-ocr", GlmContext = 1024, GlmMaxTokens = 256 });

            var (pdf, pages) = await engine.RecognizeAsync(list, "ignored", Path.Combine(directory, "ocr"), ct);

            Assert.Equal(["INVOICE 4711", "SALMÓN 2,99 €"], pages);
            Assert.Equal(2, script.Calls);
            Assert.All(script.Prompts, prompt => Assert.Equal("Text Recognition:", prompt));
            Assert.Equal(1024, script.Context);
            Assert.Equal(256, script.MaxTokens);
            using var document = PdfDocument.Open(pdf);
            Assert.Equal(2, document.NumberOfPages);
            Assert.Contains("4711", document.GetPage(1).Text, StringComparison.Ordinal);
            // The text layer is ASCII. Search keeps the original page text, accents included.
            Assert.Contains("SALMON", document.GetPage(2).Text, StringComparison.Ordinal);
            Assert.DoesNotContain("€", document.GetPage(2).Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_unknown_engine_fails_before_any_call()
    {
        var script = new ScriptedOcr("unused");
        var engine = Engine(script, new DocumentsOptions { Engine = "nope" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RecognizeAsync("missing", "eng", "ocr", TestContext.Current.CancellationToken));
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, script.Calls);
    }

    [Fact]
    public void Page_text_keeps_letters_the_standard_font_can_draw()
    {
        Assert.Equal("plain line", GlmOcr.Clean("```markdown\nplain line\n```"));
        Assert.Equal("SALMON 2,99 ", SearchablePdf.ForStandardFont("SALMÓN 2,99 €"));
        Assert.EndsWith("/api/generate", GlmOcr.Endpoint("http://127.0.0.1:11434").AbsoluteUri, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => GlmOcr.Endpoint("glm-ocr"));
    }

    [Fact]
    public async Task A_truncated_response_fails_with_the_limit_named()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pdn_glm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var image = Png(directory, "page.png");
            var engine = Engine(new ScriptedOcr("half a page") { DoneReason = "length" }, new DocumentsOptions { Engine = "GLM" });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RecognizeAsync(image, "eng", Path.Combine(directory, "ocr"), ct));
            Assert.Contains("GlmContext", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OcrEngine Engine(ScriptedOcr script, DocumentsOptions options) =>
        new(Options.Create(options), new OneClient(script));

    private static string Png(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        using var bitmap = new SKBitmap(30, 16);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
        return path;
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ScriptedOcr(params string[] pages) : HttpMessageHandler
    {
        private int index;

        public List<string> Prompts { get; } = [];

        public int Calls { get; private set; }

        public int Context { get; private set; }

        public int MaxTokens { get; private set; }

        public string DoneReason { get; init; } = "stop";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Prompts.Add(json.GetProperty("prompt").GetString()!);
            Context = json.GetProperty("options").GetProperty("num_ctx").GetInt32();
            MaxTokens = json.GetProperty("options").GetProperty("num_predict").GetInt32();
            Assert.Equal(0, json.GetProperty("options").GetProperty("temperature").GetDouble());
            Assert.False(json.GetProperty("stream").GetBoolean());
            Assert.EndsWith("/api/generate", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            Calls++;
            var text = pages[Math.Min(index, pages.Length - 1)];
            index++;
            var body = JsonSerializer.Serialize(new { response = text, done_reason = DoneReason });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
