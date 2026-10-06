using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Configuration;
using PaperDotNet.Documents.Features.PhotoToDocument;
using PaperDotNet.Documents.Features.StorageOptimization;
using PaperDotNet.Ocr;
using PaperDotNet.Ocr.Contracts;
using SkiaSharp;
using UglyToad.PdfPig;

namespace PaperDotNet.UnitTests;

public sealed class PhotoNormalizationTests
{
    [Fact]
    public async Task Resized_photo_pdf_has_savings_comparable_to_image_optimization_at_the_same_text_target()
    {
        var directory = Directory.CreateTempSubdirectory("pdn_compressed_photo_");
        try
        {
            using var bitmap = new SKBitmap(1600, 800);
            var random = new Random(4711);
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var value = (byte)Math.Clamp(180 + x / 40 + random.Next(-35, 36), 0, 255);
                    bitmap.SetPixel(x, y, new SKColor(value, (byte)(value - 20), (byte)(value - 40)));
                }
            using var image = SKImage.FromBitmap(bitmap);
            using var original = image.Encode(SKEncodedImageFormat.Jpeg, 98);
            var source = Path.Combine(directory.FullName, "photo.jpg");
            await File.WriteAllBytesAsync(source, original.ToArray(), TestContext.Current.CancellationToken);
            var config = new ConfigurationBuilder().Build();
            var detector = new PhotoTextDetector(1600, 96, expectWhite: false);
            var pagePath = Path.Combine(directory.FullName, "page.png");
            var pdfSize = await ComposePhotosActivity.PreparePageAsync(source, pagePath, "deu", new(config, detector, detector), TestContext.Current.CancellationToken);
            using var page = SKBitmap.Decode(pagePath);
            Assert.Equal(1600, page.Width);
            Assert.Equal(800, page.Height);
            Assert.Equal(533, pdfSize.Width);
            Assert.Equal(267, pdfSize.Height);
            var pdfPath = Path.Combine(directory.FullName, "photo.pdf");
            const string text = "RECEIPT 4711";
            PaddleSearchablePdf.Write([new(pagePath, text, [new(text, [new(60, 60), new(900, 60), new(900, 156), new(60, 156)])], pdfSize)],
                pdfPath, TestContext.Current.CancellationToken);
            using var gate = new ImageOptimizationGate();
            var optimizer = new ImageOptimizationAdapter(config, gate, detector, detector);
            await using var input = File.OpenRead(source);
            var optimized = await optimizer.OptimizeAsync(input, "deu", new(TargetHeight: 32, Percentile: 0), TestContext.Current.CancellationToken);
            using var optimizedContent = optimized.Content;
            Assert.NotNull(optimizedContent);
            var pdfBytes = new FileInfo(pdfPath).Length;
            Assert.True(pdfBytes < original.Size * 0.15, $"Source: {original.Size}; PDF: {pdfBytes} bytes.");
            Assert.True(pdfBytes < optimizedContent.Length * 3 + 8192,
                $"Optimized WebP: {optimizedContent.Length}; PDF: {pdfBytes} bytes.");
            using var pdf = PdfDocument.Open(pdfPath);
            Assert.Equal(text, pdf.GetPage(1).Text);
            var embedded = Assert.Single(pdf.GetPage(1).GetImages());
            Assert.Equal(533, embedded.WidthInSamples);
            Assert.Equal(267, embedded.HeightInSamples);
            Assert.Contains("/DCTDecode", Encoding.Latin1.GetString(await File.ReadAllBytesAsync(pdfPath, TestContext.Current.CancellationToken)), StringComparison.Ordinal);
            using var decoded = SKBitmap.Decode(embedded.RawBytes.ToArray());
            Assert.NotNull(decoded);
            Assert.Equal(pdfSize.Width, decoded.Width);
            using var expected = page.Resize(new SKImageInfo(pdfSize.Width, pdfSize.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            Assert.InRange(Math.Abs(expected.GetPixel(100, 100).Red - decoded.GetPixel(100, 100).Red), 0, 15);
            Assert.InRange(pdf.GetPage(1).Letters[0].BoundingBox.Height, 15.37, 15.39);
            TestContext.Current.TestOutputHelper?.WriteLine($"Source JPEG: {original.Size}; optimized WebP: {optimizedContent.Length}; PDF: {pdfBytes} bytes.");
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(640, 64, 320, "paddleocr")]
    [InlineData(640, 16, 1280, "paddleocr")]
    [InlineData(640, 32, 640, "paddleocr")]
    [InlineData(640, 0, 640, "paddleocr")]
    [InlineData(3000, 64, 1500, "paddleocr")]
    [InlineData(3000, 64, 1500, "tesseract")]
    public async Task Original_pixels_are_kept_for_ocr_and_only_pdf_sizes_target_32px_text(
        int sourceWidth, double smallestTextHeight, int expectedWidth, string engine)
    {
        var directory = Directory.CreateTempSubdirectory("pdn_resize_photo_");
        try
        {
            using var bitmap = new SKBitmap(sourceWidth, sourceWidth / 2);
            bitmap.Erase(SKColors.Transparent);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var source = Path.Combine(directory.FullName, "source.png");
            var bytes = encoded.ToArray();
            await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);
            var target = Path.Combine(directory.FullName, "page.png");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StorageOptimization:TextDetector"] = engine,
            }).Build();
            var detector = new PhotoTextDetector(sourceWidth, smallestTextHeight);
            var analysis = new ImageTextAnalysis(config, detector, detector);
            var pdfSize = await ComposePhotosActivity.PreparePageAsync(source, target, "deu", analysis, TestContext.Current.CancellationToken);
            using var result = SKBitmap.Decode(target);
            Assert.Equal(sourceWidth, result.Width);
            Assert.Equal(sourceWidth / 2, result.Height);
            Assert.Equal(expectedWidth, pdfSize.Width);
            Assert.Equal(expectedWidth / 2, pdfSize.Height);
            Assert.Equal(SKColors.White, result.GetPixel(0, 0));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
            Assert.Equal(engine, detector.Engine);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class PhotoTextDetector(int width, double smallestHeight, bool expectWhite = true) : ITextDetector, IWordLayoutDetector
    {
        public string Model => "test";
        public string? ModelDigest => null;
        public string? Engine { get; private set; }
        private IReadOnlyList<double> Heights => smallestHeight == 0 ? [] : [smallestHeight, smallestHeight * 2];

        public Task<IReadOnlyList<double>> DetectAsync(Stream image, int sourceWidth, int sourceHeight,
            double confidence, CancellationToken cancellationToken)
        {
            Engine = "paddleocr";
            Assert.Equal(width, sourceWidth);
            Assert.Equal(width / 2, sourceHeight);
            AssertAnalysis(image, confidence);
            return Task.FromResult(Heights);
        }

        public Task<IReadOnlyList<double>> DetectAsync(Stream image, string languages, double scale,
            double confidence, CancellationToken cancellationToken)
        {
            Engine = "tesseract";
            Assert.Equal("deu", languages);
            Assert.Equal(Math.Min(1, 2600.0 / width), scale);
            AssertAnalysis(image, confidence);
            return Task.FromResult(Heights);
        }

        private void AssertAnalysis(Stream image, double confidence)
        {
            using var bitmap = SKBitmap.Decode(image);
            Assert.Equal(Math.Min(width, 2600), bitmap.Width);
            if (expectWhite) Assert.Equal(SKColors.White, bitmap.GetPixel(0, 0));
            Assert.Equal(50, confidence);
        }
    }

    [Fact]
    public void Single_frame_animated_webp_is_rejected()
    {
        var directory = Directory.CreateTempSubdirectory("pdn_animated_webp_");
        try
        {
            using var bitmap = new SKBitmap(20, 10);
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 90);
            var frame = encoded.ToArray()[12..];
            using var chunks = new MemoryStream();
            using (var writer = new BinaryWriter(chunks, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("VP8X"u8);
                writer.Write(10);
                writer.Write(new byte[] { 2, 0, 0, 0, 19, 0, 0, 9, 0, 0 });
                writer.Write("ANIM"u8);
                writer.Write(6);
                writer.Write(new byte[6]);
                writer.Write("ANMF"u8);
                writer.Write(16 + frame.Length);
                writer.Write(new byte[] { 0, 0, 0, 0, 0, 0, 19, 0, 0, 9, 0, 0, 10, 0, 0, 0 });
                writer.Write(frame);
            }
            var source = Path.Combine(directory.FullName, "animated.webp");
            using (var file = File.Create(source))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write("RIFF"u8);
                writer.Write(4 + (int)chunks.Length);
                writer.Write("WEBP"u8);
                writer.Write(chunks.ToArray());
            }
            using var codec = SKCodec.Create(source);
            Assert.NotNull(codec);
            Assert.Equal(1, codec.FrameCount);
            var error = Assert.Throws<InvalidOperationException>(() => ComposePhotosActivity.Normalize(source, Path.Combine(directory.FullName, "page.png")));
            Assert.Contains("Animated", error.Message, StringComparison.Ordinal);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void Static_images_are_flattened_to_white(SKEncodedImageFormat format)
    {
        var directory = Directory.CreateTempSubdirectory("pdn_static_photo_");
        try
        {
            using var bitmap = new SKBitmap(20, 10);
            bitmap.Erase(SKColors.Transparent);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(format, 100);
            var source = Path.Combine(directory.FullName, "source");
            File.WriteAllBytes(source, encoded.ToArray());
            var target = Path.Combine(directory.FullName, "page.png");
            ComposePhotosActivity.Normalize(source, target);
            using var result = SKBitmap.Decode(target);
            Assert.Equal(SKColors.White, result.GetPixel(5, 5));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Animated_png_is_rejected_even_when_decoder_exposes_only_the_default_image()
    {
        var directory = Directory.CreateTempSubdirectory("pdn_animated_photo_");
        try
        {
            using var bitmap = new SKBitmap(20, 10);
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = encoded.ToArray();
            var chunk = new byte[20];
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), 8);
            "acTL"u8.CopyTo(chunk.AsSpan(4));
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8, 4), 2);
            uint crc = uint.MaxValue;
            foreach (var value in chunk.AsSpan(4, 12))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            }
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16), ~crc);
            var source = Path.Combine(directory.FullName, "animated.png");
            using (var file = File.Create(source))
            {
                file.Write(png.AsSpan(0, 33));
                file.Write(chunk);
                file.Write(png.AsSpan(33));
            }
            var error = Assert.Throws<InvalidOperationException>(() => ComposePhotosActivity.Normalize(source, Path.Combine(directory.FullName, "page.png")));
            Assert.Contains("Animated", error.Message, StringComparison.Ordinal);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(1, 100, 60, "red")]
    [InlineData(2, 100, 60, "blue")]
    [InlineData(3, 100, 60, "yellow")]
    [InlineData(4, 100, 60, "green")]
    [InlineData(5, 60, 100, "red")]
    [InlineData(6, 60, 100, "green")]
    [InlineData(7, 60, 100, "yellow")]
    [InlineData(8, 60, 100, "blue")]
    public void Jpeg_exif_orientation_is_applied_before_ocr(int orientation, int width, int height, string corner)
    {
        var directory = Directory.CreateTempSubdirectory("pdn_orientation_");
        try
        {
            using var bitmap = new SKBitmap(100, 60);
            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint();
            foreach (var (x, y, color) in new[] { (0, 0, SKColors.Red), (50, 0, SKColors.Blue), (0, 30, SKColors.Lime), (50, 30, SKColors.Yellow) })
            {
                paint.Color = color;
                canvas.DrawRect(x, y, 50, 30, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
            var jpeg = encoded.ToArray();
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("Exif\0\0"u8);
                writer.Write("II"u8);
                writer.Write((ushort)42);
                writer.Write(8);
                writer.Write((ushort)1);
                writer.Write((ushort)0x112);
                writer.Write((ushort)3);
                writer.Write(1);
                writer.Write((ushort)orientation);
                writer.Write((ushort)0);
                writer.Write(0);
            }
            var source = Path.Combine(directory.FullName, "photo.jpg");
            using (var file = File.Create(source))
            {
                file.Write(jpeg.AsSpan(0, 2));
                file.Write(new byte[] { 255, 225, 0, 34 });
                file.Write(payload.ToArray());
                file.Write(jpeg.AsSpan(2));
            }
            var target = Path.Combine(directory.FullName, "page.png");
            ComposePhotosActivity.Normalize(source, target);
            using var result = SKBitmap.Decode(target);
            Assert.Equal(width, result.Width);
            Assert.Equal(height, result.Height);
            var expected = corner switch { "red" => SKColors.Red, "blue" => SKColors.Blue, "green" => SKColors.Lime, _ => SKColors.Yellow };
            var actual = result.GetPixel(5, 5);
            Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 3);
            Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 3);
            Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 3);
        }
        finally { directory.Delete(recursive: true); }
    }
}
