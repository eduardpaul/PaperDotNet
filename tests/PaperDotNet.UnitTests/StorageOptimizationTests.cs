using PaperDotNet.Ocr;
using Microsoft.Extensions.Configuration;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Features.StorageOptimization;
using PaperDotNet.Documents.Features.PhotoToDocument;
using SkiaSharp;

namespace PaperDotNet.UnitTests;

public sealed class StorageOptimizationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(180)]
    public void Dbnet_oriented_height_survives_rotation_and_projects_both_axes(int angle)
    {
        var radians = angle * Math.PI / 180;
        SKPointI Rotate(int x, int y) => new((int)Math.Round(x * Math.Cos(radians) - y * Math.Sin(radians)),
            (int)Math.Round(x * Math.Sin(radians) + y * Math.Cos(radians)));
        SKPointI[] polygon = [Rotate(0, 0), Rotate(100, 0), Rotate(100, 20), Rotate(0, 20)];
        Assert.InRange(PaddleTextDetector.ProjectHeight(polygon, 0.9, 50, 2, 2)!.Value, 39, 41);
        Assert.Equal(60, PaddleTextDetector.ProjectHeight([new(0, 0), new(100, 0), new(100, 20), new(0, 20)], 0.9, 50, 2, 3));
    }

    [Fact]
    public void Dbnet_filters_low_confidence_tiny_and_square_regions()
    {
        SKPointI[] box = [new(0, 0), new(100, 0), new(100, 20), new(0, 20)];
        Assert.Null(PaddleTextDetector.ProjectHeight(box, 0.49, 50, 1, 1));
        Assert.Null(PaddleTextDetector.ProjectHeight(box, double.NaN, 50, 1, 1));
        Assert.Null(PaddleTextDetector.ProjectHeight([new(0, 0), new(100, 0), new(100, 5), new(0, 5)], 0.9, 50, 1, 1));
        Assert.Null(PaddleTextDetector.ProjectHeight([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], 0.9, 50, 1, 1));
        Assert.Null(PaddleTextDetector.ProjectHeight([], 0.9, 50, 1, 1));
        Assert.Equal(32, PaddleTextDetector.NetworkDimension(1));
        Assert.Equal(2592, PaddleTextDetector.NetworkDimension(2600));
    }

    [Fact]
    public async Task Default_dbnet_runs_offline_without_tesseract_and_blank_image_keeps_original()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Documents:TesseractPath"] = "nonexistent-tesseract",
        }).Build();
        using var gate = new ImageOptimizationGate();
        using var detector = new PaddleTextDetector(config);
        var adapter = new ImageOptimizationAdapter(config, gate, detector, new TesseractWordDetector(Microsoft.Extensions.Options.Options.Create(new OcrOptions { TesseractPath = config["Documents:TesseractPath"] ?? "tesseract" })));
        using var bitmap = new SKBitmap(640, 640);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var blankImage = SKImage.FromBitmap(bitmap);
        using var blankPng = blankImage.Encode(SKEncodedImageFormat.Png, 100);
        await using var blank = new MemoryStream(blankPng.ToArray());
        await using var skipped = await adapter.OptimizeAsync(blank, null, new(), TestContext.Current.CancellationToken);
        Assert.Null(skipped.Content);
        Assert.Equal("paddleocr", skipped.Metrics["detector"]!.GetValue<string>());
        Assert.Equal("PP-OCRv6_small_det", skipped.Metrics["model"]!.GetValue<string>());
        Assert.Equal("line", skipped.Metrics["strategy"]!.GetValue<string>());
        Assert.Equal("d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e", skipped.Metrics["modelSha256"]!.GetValue<string>());
        Assert.Contains("No reliable text", skipped.SkipReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dbnet_timeout_retains_source_releases_gate_and_next_call_succeeds()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StorageOptimization:OcrTimeoutSeconds"] = "0.001",
        }).Build();
        using var gate = new ImageOptimizationGate();
        using var detector = new PaddleTextDetector(config);
        var adapter = new ImageOptimizationAdapter(config, gate, detector, new TesseractWordDetector(Microsoft.Extensions.Options.Options.Create(new OcrOptions { TesseractPath = config["Documents:TesseractPath"] ?? "tesseract" })));
        using var bitmap = new SKBitmap(640, 640);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        await using var source = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.OptimizeAsync(source, null, new(), TestContext.Current.CancellationToken));
        Assert.Contains("OCR timeout", error.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, source.ToArray());
        Assert.Equal(1, gate.Semaphore.CurrentCount);
        config["StorageOptimization:OcrTimeoutSeconds"] = "120";
        await using var retry = new MemoryStream(bytes);
        await using var result = await adapter.OptimizeAsync(retry, null, new(), TestContext.Current.CancellationToken);
        Assert.Null(result.Content);
        Assert.Contains("No reliable text", result.SkipReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_dbnet_model_fails_and_releases_processor_gate()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StorageOptimization:PaddleModelPath"] = "/missing/model.onnx",
        }).Build();
        using var gate = new ImageOptimizationGate();
        using var detector = new PaddleTextDetector(config);
        var adapter = new ImageOptimizationAdapter(config, gate, detector, new TesseractWordDetector(Microsoft.Extensions.Options.Options.Create(new OcrOptions { TesseractPath = config["Documents:TesseractPath"] ?? "tesseract" })));
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        await using var content = new MemoryStream(png.ToArray());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.OptimizeAsync(content, null, new(), TestContext.Current.CancellationToken));
        Assert.Contains("model is missing", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, gate.Semaphore.CurrentCount);
    }

    [Fact]
    public async Task Animated_png_is_retained_without_ocr_or_still_image_encoding()
    {
        // Two independently encoded one-pixel RGBA frames (red and blue), with valid APNG sequence and CRCs.
        const string apng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACGFjVEwAAAACAAAAAPONk3AAAAAaZmNUTAAAAAAAAAABAAAAAQAAAAAAAAAAAAEACgAAWn8w0AAAAA1JREFUeJxj+M/A8B8ABQAB/4mZPR0AAAAaZmNUTAAAAAEAAAABAAAAAQAAAAAAAAAAAAEACgAAwQzaBAAAABFmZEFUAAAAAnicY2Bg+P8fAAMCAf/1e6XXAAAAAElFTkSuQmCC";
        await using var content = new MemoryStream(Convert.FromBase64String(apng));
        using var gate = new ImageOptimizationGate();
        var config = new ConfigurationBuilder().Build();
        using var detector = new PaddleTextDetector(config);
        var adapter = new ImageOptimizationAdapter(config, gate, detector, new TesseractWordDetector(Microsoft.Extensions.Options.Options.Create(new OcrOptions { TesseractPath = config["Documents:TesseractPath"] ?? "tesseract" })));
        await using var result = await adapter.OptimizeAsync(content, "eng", new(), TestContext.Current.CancellationToken);
        Assert.Null(result.Content);
        Assert.Contains("Animated", result.SkipReason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6, 1)]
    [InlineData(24, 0.5)]
    [InlineData(1000, 0.05)]
    public void Scale_respects_target_minimum_and_no_upscaling(double textHeight, double expected) =>
        Assert.Equal(expected, ImageOptimizationAdapter.ScaleForHeight(textHeight, new()));

    [Fact]
    public void Words_do_not_fall_through_to_rows_and_noise_is_filtered_before_projection()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "4\t1\t1\t1\t1\t0\t0\t0\t400\t99\t-1\t\n"
            + "5\t1\t1\t1\t1\t1\t0\t0\t50\t20\t90\tTOTAL\n"
            + "5\t1\t1\t1\t1\t2\t0\t0\t50\t10\t70\t6.19\n"
            + "5\t1\t1\t1\t1\t3\t0\t0\t3\t1\t90\t.\n"
            + "5\t1\t1\t1\t1\t4\t0\t0\t50\t5\t49\tnoise\n";
        Assert.Equal([40.0, 20.0], TesseractWordDetector.WordHeights(tsv, 0.5, 50));
    }

    [Fact]
    public void Percentiles_interpolate_and_do_not_take_the_absolute_minimum()
    {
        Assert.Equal(15, ImageOptimizationAdapter.Percentile([10, 110], 5));
        Assert.Equal(10, ImageOptimizationAdapter.Percentile([10], 5));
        Assert.Equal(110, ImageOptimizationAdapter.Percentile([10, 110], 100));
    }

    [Theory]
    [InlineData(1, 2, 3, 1)]
    [InlineData(2, 2, 3, 2)]
    [InlineData(3, 2, 3, 4)]
    [InlineData(4, 2, 3, 3)]
    [InlineData(5, 3, 2, 1)]
    [InlineData(6, 3, 2, 2)]
    [InlineData(7, 3, 2, 4)]
    [InlineData(8, 3, 2, 3)]
    public void All_exif_orientations_preserve_pixels_including_mirrors(int origin, int width, int height, int expectedCorner)
    {
        using var image = new SKBitmap(2, 3);
        image.Erase(SKColors.White);
        image.SetPixel(0, 0, SKColors.Red);
        image.SetPixel(1, 0, SKColors.Green);
        image.SetPixel(0, 2, SKColors.Blue);
        image.SetPixel(1, 2, SKColors.Yellow);
        using var upright = ImageOptimizationAdapter.Orient(image, (SKEncodedOrigin)origin);
        Assert.Equal(width, upright.Width);
        Assert.Equal(height, upright.Height);
        var corners = new[] { upright.GetPixel(0, 0), upright.GetPixel(width - 1, 0), upright.GetPixel(0, height - 1), upright.GetPixel(width - 1, height - 1) };
        Assert.Equal(SKColors.Red, corners[expectedCorner - 1]);
    }

    [Theory]
    [InlineData(0, 50, 5, 2600, 0.05, 80)]
    [InlineData(12, 101, 5, 2600, 0.05, 80)]
    [InlineData(12, 50, -1, 2600, 0.05, 80)]
    [InlineData(12, 50, 5, 10, 0.05, 80)]
    [InlineData(12, 50, 5, 2600, 0, 80)]
    [InlineData(12, 50, 5, 2600, 0.05, 101)]
    public void Invalid_tuning_is_rejected(double target, double confidence, double percentile, int analysis, double scale, int quality) =>
        Assert.Throws<InvalidOperationException>(() => ImageOptimizationAdapter.Validate(new(target, confidence, percentile, analysis, scale, quality)));

    [Fact]
    public async Task Malformed_images_and_pixel_bombs_fail_before_ocr()
    {
        using var gate = new ImageOptimizationGate();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StorageOptimization:MaxPixels"] = "10" }).Build();
        using var detector = new PaddleTextDetector(config);
        var adapter = new ImageOptimizationAdapter(config, gate, detector, new TesseractWordDetector(Microsoft.Extensions.Options.Options.Create(new OcrOptions { TesseractPath = config["Documents:TesseractPath"] ?? "tesseract" })));
        await using var broken = new MemoryStream("broken image"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.OptimizeAsync(broken, "eng", new(), TestContext.Current.CancellationToken));
        using var bitmap = new SKBitmap(4, 4);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        await using var pixels = new MemoryStream(png.ToArray());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.OptimizeAsync(pixels, "eng", new(), TestContext.Current.CancellationToken));
        Assert.Contains("pixels", error.Message, StringComparison.Ordinal);
    }
}
