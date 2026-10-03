using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using PaperDotNet.Documents.Contracts;
using SkiaSharp;

namespace PaperDotNet.StorageOptimization;

/// <summary>The process-wide budget also applies across organizations and simultaneous workflow runs.</summary>
internal sealed class ImageOptimizationGate : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);

    public void Dispose() => Semaphore.Dispose();
}

internal sealed class ImageOptimizationAdapter(IConfiguration configuration, ImageOptimizationGate gate) : IDocumentOptimizationAdapter
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    public IReadOnlyList<string> MediaTypes => ["image/jpeg", "image/png", "image/webp"];

    public async Task<DocumentOptimizationResult> OptimizeAsync(Stream source, string? languages,
        DocumentOptimizationOptions options, CancellationToken cancellationToken)
    {
        Validate(options);
        await gate.Semaphore.WaitAsync(cancellationToken);
        DirectoryInfo? work = null;
        try
        {
            work = Directory.CreateTempSubdirectory("pdn_opt_");
            var originalPath = Path.Combine(work.FullName, "source");
            await using (var file = File.Create(originalPath))
            {
                await source.CopyToAsync(file, cancellationToken);
            }

            var originalSize = new FileInfo(originalPath).Length;
            using var encoded = File.OpenRead(originalPath);
            using var codec = SKCodec.Create(encoded) ?? throw new InvalidOperationException("The image cannot be decoded.");
            var maxPixels = configuration.GetValue<long?>("StorageOptimization:MaxPixels") ?? 64_000_000;
            if ((long)codec.Info.Width * codec.Info.Height > maxPixels)
            {
                throw new InvalidOperationException($"The image exceeds the optimization limit of {maxPixels} pixels.");
            }

            using var animationProbe = File.OpenRead(originalPath);
            if (codec.FrameCount > 1 || IsAnimatedPng(animationProbe))
            {
                return new(null, "image/webp", ".webp", [], "Animated images are retained unchanged.");
            }

            using var decoded = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            {
                throw new InvalidOperationException("The image is malformed or incomplete.");
            }

            using var upright = Orient(decoded, codec.EncodedOrigin);
            var k = Math.Min(1.0, options.MaxAnalysisDimension / (double)Math.Max(upright.Width, upright.Height));
            using var analysis = Resize(upright, Math.Max(1, (int)Math.Round(upright.Width * k)), Math.Max(1, (int)Math.Round(upright.Height * k)));
            var analysisPath = Path.Combine(work.FullName, "analysis.png");
            using (var image = SKImage.FromBitmap(analysis))
            using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
            await using (var output = File.Create(analysisPath))
            {
                png.SaveTo(output);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(configuration.GetValue<double?>("StorageOptimization:OcrTimeoutSeconds") ?? 120));
            var outputBase = Path.Combine(work.FullName, "layout");
            var language = string.IsNullOrWhiteSpace(languages) ? "eng" : languages;
            BufferedCommandResult result;
            try
            {
                result = await Cli.Wrap(configuration["Documents:TesseractPath"] ?? "tesseract")
                    .WithArguments([analysisPath, outputBase, "-l", language, "tsv"])
                    .WithEnvironmentVariables(e => e.Set("OMP_THREAD_LIMIT", "1"))
                    .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(timeout.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Text analysis exceeded the configured OCR timeout; the source is retained.", ex);
            }
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Text analysis failed: {result.StandardError.Trim()[..Math.Min(500, result.StandardError.Trim().Length)]}");
            }

            if (!File.Exists(outputBase + ".tsv"))
            {
                throw new InvalidOperationException($"The OCR engine did not write layout data: {result.StandardError.Trim()}");
            }

            var tsv = await File.ReadAllTextAsync(outputBase + ".tsv", cancellationToken);
            var heights = WordHeights(tsv, k, options.MinConfidence);
            if (heights.Count == 0)
            {
                return new(null, "image/webp", ".webp", [], "No reliable text was detected; the original is retained.");
            }

            var smallest = Math.Max(4, Percentile(heights, options.Percentile));
            var scale = ScaleForHeight(smallest, options);
            var width = Math.Max(1, (int)Math.Round(upright.Width * scale));
            var height = Math.Max(1, (int)Math.Round(upright.Height * scale));
            using var resized = Resize(upright, width, height);
            using var outputImage = SKImage.FromBitmap(resized);
            using var data = outputImage.Encode(SKEncodedImageFormat.Webp, options.Quality);
            var metrics = new JsonObject
            {
                ["sourceWidth"] = upright.Width,
                ["sourceHeight"] = upright.Height,
                ["width"] = width,
                ["height"] = height,
                ["sourceBytes"] = originalSize,
                ["bytes"] = data.Size,
                ["scale"] = scale,
                ["smallestTextHeight"] = smallest,
                ["savingsPercent"] = 100.0 * (originalSize - data.Size) / originalSize,
                ["strategy"] = "word",
                ["targetHeight"] = options.TargetHeight,
                ["confidence"] = options.MinConfidence,
                ["percentile"] = options.Percentile,
                ["quality"] = options.Quality,
                ["minimumScale"] = options.MinimumScale,
                ["analysisDimension"] = options.MaxAnalysisDimension,
                ["analysisScale"] = k,
                ["words"] = heights.Count,
            };
            return data.Size >= originalSize
                ? new(null, "image/webp", ".webp", metrics, "The candidate is not smaller; the original is retained.")
                : new(new MemoryStream(data.ToArray(), writable: false), "image/webp", ".webp", metrics);
        }
        finally
        {
            try
            {
                work?.Delete(recursive: true);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
    }

    internal static double ScaleForHeight(double height, DocumentOptimizationOptions options) =>
        Math.Clamp(options.TargetHeight / height, options.MinimumScale, 1);

    /// <summary>Skia's PNG codec exposes only the default image of APNG. Its animation control precedes IDAT.</summary>
    internal static bool IsAnimatedPng(Stream content)
    {
        Span<byte> header = stackalloc byte[8];
        if (content.Read(header) != header.Length || !header.SequenceEqual(PngSignature))
        {
            return false;
        }

        while (content.Position + header.Length <= content.Length)
        {
            content.ReadExactly(header);
            if (header[4..].SequenceEqual("acTL"u8))
            {
                return true;
            }

            if (header[4..].SequenceEqual("IDAT"u8) || header[4..].SequenceEqual("IEND"u8))
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length > content.Length - content.Position - 4)
            {
                return false;
            }

            content.Seek((long)length + 4, SeekOrigin.Current);
        }

        return false;
    }

    internal static void Validate(DocumentOptimizationOptions options)
    {
        if (!double.IsFinite(options.TargetHeight) || options.TargetHeight < 4 || options.TargetHeight > 200
            || !double.IsFinite(options.MinConfidence) || options.MinConfidence < 0 || options.MinConfidence > 100
            || !double.IsFinite(options.Percentile) || options.Percentile < 0 || options.Percentile > 100
            || !double.IsFinite(options.MinimumScale) || options.MinimumScale <= 0 || options.MinimumScale > 1
            || options.MaxAnalysisDimension < 256 || options.MaxAnalysisDimension > 10000 || options.Quality < 1 || options.Quality > 100)
        {
            throw new InvalidOperationException("Invalid optimization settings: target 4–200px, confidence/percentile 0–100, scale (0,1], analysis 256–10000px, quality 1–100.");
        }
    }

    internal static List<double> WordHeights(string tsv, double scale, double minConfidence)
    {
        var heights = new List<double>();
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var cells = line.TrimEnd('\r').Split('\t', 12);
            if (cells.Length != 12 || cells[0] != "5"
                || !double.TryParse(cells[10], CultureInfo.InvariantCulture, out var confidence) || confidence < minConfidence
                || !double.TryParse(cells[9], CultureInfo.InvariantCulture, out var height)
                || cells[11].Count(char.IsLetterOrDigit) < 2)
            {
                continue;
            }

            var projected = Math.Round(height / scale);
            if (projected > 3)
            {
                heights.Add(projected);
            }
        }

        return heights;
    }

    internal static double Percentile(IReadOnlyList<double> heights, double percentile)
    {
        var sorted = heights.Order().ToArray();
        var index = percentile / 100 * (sorted.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return Math.Round(sorted[lower] * (1 - (index - lower)) + sorted[upper] * (index - lower));
    }

    internal static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var output = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(output);
        var w = source.Width;
        var h = source.Height;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return output;
    }

    private static SKBitmap Resize(SKBitmap image, int width, int height) =>
        image.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
        ?? throw new InvalidOperationException("The image cannot be resized.");
}
