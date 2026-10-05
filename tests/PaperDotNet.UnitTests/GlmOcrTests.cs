using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PaperDotNet.Ocr;
using SkiaSharp;
using UglyToad.PdfPig;

namespace PaperDotNet.UnitTests;

public sealed class GlmOcrTests
{
    [Fact]
    public async Task Glm_recognizes_original_dimensions_before_pdf_downsampling()
    {
        var directory = Directory.CreateTempSubdirectory("pdn_glm_original_");
        try
        {
            var path = Png(directory.FullName, "original.png");
            var script = new ScriptedOcr("INVOICE 4711");
            var engine = Engine(script, new OcrOptions { Engine = "glm" });
            var result = await engine.RecognizeAsync(path, "eng", Path.Combine(directory.FullName, "result"),
                [new(15, 8)], TestContext.Current.CancellationToken);
            Assert.Equal(30, script.ImageWidth);
            Assert.Equal(16, script.ImageHeight);
            Assert.Equal("INVOICE 4711", Assert.Single(result.Pages));
            using var pdf = PdfDocument.Open(result.Pdf);
            Assert.Equal("INVOICE 4711", pdf.GetPage(1).Text);
            var image = Assert.Single(pdf.GetPage(1).GetImages());
            Assert.Equal(15, image.WidthInSamples);
            Assert.Equal(8, image.HeightInSamples);
        }
        finally { directory.Delete(recursive: true); }
    }

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
            var engine = Engine(script, new OcrOptions { Engine = "glm", GlmModel = "glm-ocr", GlmContext = 1024, GlmMaxTokens = 256 });

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
        var engine = Engine(script, new OcrOptions { Engine = "nope" });
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
            var engine = Engine(new ScriptedOcr("half a page") { DoneReason = "length" }, new OcrOptions { Engine = "GLM" });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RecognizeAsync(image, "eng", Path.Combine(directory, "ocr"), ct));
            Assert.Contains("GlmContext", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Composition_can_keep_a_blank_page_after_successful_ocr()
    {
        var directory = Directory.CreateTempSubdirectory("pdn_blank_glm_");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var source = Png(directory.FullName, "page.png");
            var engine = Engine(new ScriptedOcr(""), new OcrOptions { Engine = "glm" });
            var (pdf, pages) = await engine.RecognizeAsync(source, "eng", Path.Combine(directory.FullName, "result"), ct, allowEmpty: true);
            Assert.Equal("", Assert.Single(pages));
            using var document = PdfDocument.Open(pdf);
            Assert.Equal(1, document.NumberOfPages);
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RecognizeAsync(source, "eng", Path.Combine(directory.FullName, "ordinary"), ct));
            var missingResponse = Engine(new ScriptedOcr([null!]), new OcrOptions { Engine = "glm" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => missingResponse.RecognizeAsync(source, "eng", Path.Combine(directory.FullName, "missing"), ct, allowEmpty: true));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static OcrEngine Engine(ScriptedOcr script, OcrOptions options) =>
        new(Options.Create(options), new OneClient(script), new PaddleOcr(new ConfigurationBuilder().Build()));

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
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }

        public int Context { get; private set; }

        public int MaxTokens { get; private set; }

        public string DoneReason { get; init; } = "stop";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            using var image = SKBitmap.Decode(Convert.FromBase64String(json.GetProperty("images")[0].GetString()!));
            ImageWidth = image.Width;
            ImageHeight = image.Height;
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
