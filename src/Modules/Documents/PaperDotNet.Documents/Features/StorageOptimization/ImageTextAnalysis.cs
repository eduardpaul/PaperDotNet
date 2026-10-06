using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Ocr.Contracts;
using SkiaSharp;

namespace PaperDotNet.Documents.Features.StorageOptimization;

internal sealed class ImageTextAnalysis(IConfiguration configuration, ITextDetector detector, IWordLayoutDetector words)
{
    public async Task<(IReadOnlyList<double> Heights, JsonObject Metrics)> AnalyzeAsync(SKBitmap image, string? languages,
        DocumentOptimizationOptions options, CancellationToken cancellationToken)
    {
        var k = Math.Min(1.0, options.MaxAnalysisDimension / (double)Math.Max(image.Width, image.Height));
        using var analysis = Resize(image, Math.Max(1, (int)Math.Round(image.Width * k)), Math.Max(1, (int)Math.Round(image.Height * k)));
        var engine = (configuration["StorageOptimization:TextDetector"] ?? "paddleocr").ToLowerInvariant();
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.GetValue<double?>("StorageOptimization:OcrTimeoutSeconds") ?? 120));
        IReadOnlyList<double> heights;
        try
        {
            heights = engine switch
            {
                "paddleocr" => await DetectLinesAsync(analysis, image.Width, image.Height, options.MinConfidence, timeout.Token),
                "tesseract" => await DetectWordsAsync(analysis, languages, k, options.MinConfidence, timeout.Token),
                _ => throw new InvalidOperationException("StorageOptimization:TextDetector must be paddleocr or tesseract."),
            };
            timeout.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Text analysis exceeded the configured OCR timeout; the source is retained.", ex);
        }
        var metrics = new JsonObject
        {
            ["detector"] = engine,
            ["model"] = engine == "paddleocr" ? detector.Model : null,
            ["modelSha256"] = engine == "paddleocr" ? detector.ModelDigest : null,
            ["strategy"] = engine == "paddleocr" ? "line" : "word",
            ["textRegions"] = heights.Count,
            ["analysisMilliseconds"] = timer.Elapsed.TotalMilliseconds,
            ["confidence"] = options.MinConfidence,
            ["analysisDimension"] = options.MaxAnalysisDimension,
            ["analysisScale"] = k,
        };
        return (heights, metrics);
    }

    private async Task<IReadOnlyList<double>> DetectLinesAsync(SKBitmap analysis, int width, int height, double confidence, CancellationToken ct)
    {
        using var image = SKImage.FromBitmap(analysis);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var input = new MemoryStream(png.ToArray(), writable: false);
        return await detector.DetectAsync(input, width, height, confidence, ct);
    }

    private async Task<IReadOnlyList<double>> DetectWordsAsync(SKBitmap analysis, string? languages,
        double scale, double minConfidence, CancellationToken cancellationToken)
    {
        using var image = SKImage.FromBitmap(analysis);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var input = new MemoryStream(png.ToArray(), writable: false);
        return await words.DetectAsync(input, string.IsNullOrWhiteSpace(languages) ? "eng" : languages, scale, minConfidence, cancellationToken);
    }

    private static SKBitmap Resize(SKBitmap image, int width, int height) =>
        image.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
        ?? throw new InvalidOperationException("The image cannot be resized.");
}
