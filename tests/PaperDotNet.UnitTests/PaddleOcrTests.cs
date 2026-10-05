using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Ocr;
using PaperDotNet.Ocr.Contracts;
using SkiaSharp;
using UglyToad.PdfPig;

namespace PaperDotNet.UnitTests;

public sealed class PaddleOcrTests
{
    [Fact]
    public async Task Pdf_downsampling_does_not_change_text_recognized_from_original_pixels()
    {
        var work = Directory.CreateTempSubdirectory("pdn_original_recognition_");
        try
        {
            var path = Image(work.FullName, "original.png", "INVOICE 4711", 0);
            var config = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            new OcrModule().AddServices(services, config);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IOcrService>();
            await using var input = File.OpenRead(path);
            await using var original = await service.RecognizeAsync([input], "eng", TestContext.Current.CancellationToken);
            input.Position = 0;
            // At 40x20 the visible invoice text is unreadable; OCR must still use the 800x400 input.
            await using var tiny = await service.RecognizeAsync([input], "eng", [new(40, 20)], TestContext.Current.CancellationToken);
            Assert.Equal(original.PageTexts, tiny.PageTexts);
            Assert.Contains("INVOICE 4711", tiny.PageTexts.Single(), StringComparison.Ordinal);
            using var pdf = PdfDocument.Open(tiny.Pdf);
            Assert.Contains("INVOICE 4711", pdf.GetPage(1).Text, StringComparison.Ordinal);
            var image = Assert.Single(pdf.GetPage(1).GetImages());
            Assert.Equal(40, image.WidthInSamples);
            Assert.Equal(20, image.HeightInSamples);
            Assert.All(pdf.GetPage(1).Letters, letter =>
            {
                Assert.InRange(letter.StartBaseLine.X, 0, pdf.GetPage(1).Width);
                Assert.InRange(letter.StartBaseLine.Y, 0, pdf.GetPage(1).Height);
            });
        }
        finally { work.Delete(recursive: true); }
    }

    [Fact]
    public void Recognition_crops_retain_original_pixel_detail_and_reject_unbounded_lines()
    {
        using var original = new SKBitmap(1600, 800);
        original.Erase(SKColors.White);
        for (var x = 400; x < 600; x++)
            for (var y = 200; y < 300; y++)
                original.SetPixel(x, y, x % 2 == 0 ? SKColors.Black : SKColors.White);
        SKPointI[] points = [new(400, 200), new(600, 200), new(600, 300), new(400, 300)];
        using var crop = PaddleTextCrop.Create(original, points, out var readingOrder);
        Assert.Equal(200, crop.Width);
        Assert.Equal(100, crop.Height);
        Assert.Equal(points, readingOrder);
        Assert.True(Math.Abs(crop.GetPixel(20, 50).Red - crop.GetPixel(21, 50).Red) > 200);
        Assert.Throws<InvalidOperationException>(() => PaddleTextCrop.Create(original,
            [new(0, 0), new(1500, 0), new(1500, 2), new(0, 2)], out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    [InlineData(90)]
    [InlineData(270)]
    public async Task Default_pipeline_recognizes_ordered_pages_offline_without_tesseract(int angle)
    {
        var work = Directory.CreateTempSubdirectory("pdn_paddle_test_");
        try
        {
            var first = Image(work.FullName, "first.png", "INVOICE 4711", angle);
            var second = Image(work.FullName, "second.png", "TOTAL 1234", 0);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ocr:TesseractPath"] = "not-installed-tesseract",
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            new OcrModule().AddServices(services, config);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            Assert.Equal("paddleocr", provider.GetRequiredService<IOptions<OcrOptions>>().Value.Engine);
            await using var page1 = File.OpenRead(first);
            await using var page2 = File.OpenRead(second);
            await using var result = await scope.ServiceProvider.GetRequiredService<IOcrService>()
                .RecognizeAsync([page1, page2], "eng", TestContext.Current.CancellationToken);
            Assert.Equal(2, result.PageTexts.Count);
            Assert.Contains("4711", result.PageTexts[0], StringComparison.Ordinal);
            Assert.Contains("1234", result.PageTexts[1], StringComparison.Ordinal);
            using var pdf = PdfDocument.Open(result.Pdf);
            Assert.Equal(2, pdf.NumberOfPages);
            Assert.Contains("INVOICE 4711", pdf.GetPage(1).Text, StringComparison.Ordinal);
            Assert.Contains("1234", pdf.GetPage(2).Text, StringComparison.Ordinal);
            Assert.Single(pdf.GetPage(1).GetImages());
        }
        finally { work.Delete(recursive: true); }
    }

    [Fact]
    public async Task Blank_page_is_kept_only_when_requested_and_cancellation_releases_gate()
    {
        var work = Directory.CreateTempSubdirectory("pdn_paddle_blank_");
        try
        {
            var image = Image(work.FullName, "blank.png", "", 0);
            using var paddle = new PaddleOcr(new ConfigurationBuilder().Build());
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                paddle.RecognizeAsync(image, Path.Combine(work.FullName, "cancelled"), cancelled.Token));
            using var duringInference = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                paddle.RecognizeAsync(image, Path.Combine(work.FullName, "timeout"), duringInference.Token, allowEmpty: true));
            var (pdf, texts) = await paddle.RecognizeAsync(image, Path.Combine(work.FullName, "allowed"), TestContext.Current.CancellationToken, allowEmpty: true);
            Assert.Equal("", Assert.Single(texts));
            using var document = PdfDocument.Open(pdf);
            Assert.Equal(1, document.NumberOfPages);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                paddle.RecognizeAsync(image, Path.Combine(work.FullName, "rejected"), TestContext.Current.CancellationToken));
        }
        finally { work.Delete(recursive: true); }
    }

    [Fact]
    public async Task Missing_models_fail_even_for_blank_pages_and_retry_can_load_the_bundled_model()
    {
        var work = Directory.CreateTempSubdirectory("pdn_paddle_missing_");
        try
        {
            var image = Image(work.FullName, "blank.png", "", 0);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ocr:PaddleRecognitionModelPath"] = "/missing/recognizer.onnx",
            }).Build();
            using var paddle = new PaddleOcr(config);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                paddle.RecognizeAsync(image, Path.Combine(work.FullName, "missing"), TestContext.Current.CancellationToken, allowEmpty: true));
            Assert.Contains("missing", error.Message, StringComparison.Ordinal);
            config["Ocr:PaddleRecognitionModelPath"] = null;
            var (_, texts) = await paddle.RecognizeAsync(image, Path.Combine(work.FullName, "retry"), TestContext.Current.CancellationToken, allowEmpty: true);
            Assert.Equal("", Assert.Single(texts));
        }
        finally { work.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_tall_receipt_does_not_bypass_the_detection_memory_bound()
    {
        var work = Directory.CreateTempSubdirectory("pdn_paddle_tall_");
        try
        {
            using var bitmap = new SKBitmap(160, 4000);
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var source = Path.Combine(work.FullName, "receipt.png");
            File.WriteAllBytes(source, png.ToArray());
            using var paddle = new PaddleOcr(new ConfigurationBuilder().Build());
            var (pdf, text) = await paddle.RecognizeAsync(source, Path.Combine(work.FullName, "result"), TestContext.Current.CancellationToken, allowEmpty: true);
            Assert.Equal("", Assert.Single(text));
            using var document = PdfDocument.Open(pdf);
            Assert.Equal(1920, document.GetPage(1).Height);
            Assert.Equal(76.8, document.GetPage(1).Width, precision: 1);
        }
        finally { work.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_mismatched_dictionary_fails_before_blank_pages_can_be_accepted()
    {
        var work = Directory.CreateTempSubdirectory("pdn_paddle_dictionary_");
        try
        {
            var image = Image(work.FullName, "blank.png", "", 0);
            var dictionary = Path.Combine(work.FullName, "wrong.txt");
            await File.WriteAllTextAsync(dictionary, "a\n", TestContext.Current.CancellationToken);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ocr:PaddleDictionaryPath"] = dictionary,
            }).Build();
            using var paddle = new PaddleOcr(config);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                paddle.RecognizeAsync(image, Path.Combine(work.FullName, "result"), TestContext.Current.CancellationToken, allowEmpty: true));
            Assert.Contains("do not match", error.Message, StringComparison.Ordinal);
        }
        finally { work.Delete(recursive: true); }
    }

    [Fact]
    public void Image_reader_supports_webp_and_rejects_tiff_and_nested_lists_without_new_dependencies()
    {
        var work = Directory.CreateTempSubdirectory("pdn_ocr_images_");
        try
        {
            var source = Image(work.FullName, "source.png", "", 0);
            using var bitmap = SKBitmap.Decode(source);
            using var image = SKImage.FromBitmap(bitmap);
            using var webp = image.Encode(SKEncodedImageFormat.Webp, 100);
            var input = Path.Combine(work.FullName, "page.webp");
            File.WriteAllBytes(input, webp.ToArray());
            var normalized = Assert.Single(OcrPageImages.Read(input, work.FullName, TestContext.Current.CancellationToken));
            using var result = SKBitmap.Decode(normalized);
            Assert.Equal(800, result.Width);
            Assert.Equal(400, result.Height);
            var tiff = Path.Combine(work.FullName, "page.tiff");
            File.WriteAllBytes(tiff, "II*\0"u8.ToArray());
            Assert.Contains("Ocr:Engine=tesseract", Assert.Throws<InvalidOperationException>(() =>
                OcrPageImages.Read(tiff, work.FullName, TestContext.Current.CancellationToken)).Message, StringComparison.Ordinal);
            var list = Path.Combine(work.FullName, "recursive.txt");
            File.WriteAllText(list, list);
            Assert.Throws<InvalidOperationException>(() => OcrPageImages.Read(list, work.FullName, TestContext.Current.CancellationToken));
        }
        finally { work.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pdf_retains_unicode_and_positions_text_over_the_image_without_system_fonts(bool manyCharacters)
    {
        var work = Directory.CreateTempSubdirectory("pdn_paddle_pdf_");
        try
        {
            var image = Image(work.FullName, "page.png", "", 0);
            var text = manyCharacters ? string.Concat(Enumerable.Range(0x4E00, 300).Select(c => (char)c)) + "😀" : "SALMÓN 2,99 € 中文 😀";
            SKPointI[] box = [new(40, 80), new(600, 80), new(600, 120), new(40, 120)];
            var path = Path.Combine(work.FullName, "unicode.pdf");
            PaddleSearchablePdf.Write([new(image, text, [new(text, box)])], path, TestContext.Current.CancellationToken);
            using var pdf = PdfDocument.Open(path);
            var page = pdf.GetPage(1);
            Assert.Equal(text, page.Text);
            Assert.Single(page.GetImages());
            Assert.InRange(page.Letters[0].StartBaseLine.X, 19.19, 19.21);
            Assert.InRange(page.Letters[0].StartBaseLine.Y, 134.39, 134.41);
            Assert.All(page.Letters, letter => Assert.Equal(UglyToad.PdfPig.Core.TextRenderingMode.Neither, letter.RenderingMode));
            Assert.InRange(page.Letters[0].BoundingBox.Height, 19.19, 19.21);
            Assert.Contains("/FlateDecode", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        }
        finally { work.Delete(recursive: true); }
    }

    private static string Image(string directory, string name, string text, int angle)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Rendering the OCR fixture requires Linux, Windows or macOS.");
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(384, 192);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        if (text.Length > 0) page.AddText(text, 23, new UglyToad.PdfPig.Core.PdfPoint(20, 120), font);
        using var rendered = new MemoryStream();
        // PDFtoImage uses capitalized platform annotations; the OS guard above checks supported hosts.
#pragma warning disable CA1416
        PDFtoImage.Conversion.SavePng(rendered, builder.Build(), 0, password: null, new PDFtoImage.RenderOptions { Dpi = 150 });
#pragma warning restore CA1416
        using var source = SKBitmap.Decode(rendered.ToArray());
        using var bitmap = new SKBitmap(angle is 90 or 270 ? 400 : 800, angle is 90 or 270 ? 800 : 400);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            if (angle == 90) { canvas.Translate(400, 0); canvas.RotateDegrees(90); }
            else if (angle == 270) { canvas.Translate(0, 800); canvas.RotateDegrees(270); }
            else canvas.RotateDegrees(angle, 400, 200);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, png.ToArray());
        return path;
    }
}
