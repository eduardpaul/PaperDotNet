using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace PaperDotNet.StorageOptimization;

/// <summary>One lazily loaded CPU session per process, used under the optimization gate.</summary>
internal sealed class PaddleTextDetector(IConfiguration configuration) : IDisposable
{
    internal const string ModelName = "PP-OCRv6_small_det";
    private readonly string modelPath = configuration["StorageOptimization:PaddleModelPath"]
        ?? Path.Combine(AppContext.BaseDirectory, "models", ModelName + ".onnx");
    private TextDetector? detector;
    private string? modelDigest;

    public string Model => Path.GetFileNameWithoutExtension(modelPath);
    public string? ModelDigest => modelDigest;

    public IReadOnlyList<double> Detect(SKBitmap image, int sourceWidth, int sourceHeight,
        double confidence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Paddle's nearest multiple-of-32 resize; use each axis's actual scale when projecting.
        var scale = new ScaleParam(image.Width, image.Height, NetworkDimension(image.Width), NetworkDimension(image.Height));
        // Paddle inference uses OpenCV BGR channel order. Flatten transparency onto white.
        using var bgr = new SKBitmap(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bgr))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        if (detector is null)
        {
            detector = Create(configuration, modelPath);
            using var modelContent = File.OpenRead(modelPath);
            modelDigest = Convert.ToHexStringLower(SHA256.HashData(modelContent));
        }
        var boxes = detector.GetTextBoxes(bgr, scale, (float)(confidence / 100),
            0.2f, 1.4f, cancellationToken)
            ?? throw new InvalidOperationException("PaddleOCR DBNet inference or postprocessing failed; the source is retained.");
        cancellationToken.ThrowIfCancellationRequested();
        return boxes.Select(box => ProjectHeight(box.BoxPoints, box.Score, confidence,
                sourceWidth / (double)image.Width, sourceHeight / (double)image.Height))
            .Where(height => height is not null).Select(height => height!.Value).ToArray();
    }

    internal static int NetworkDimension(int pixels) => Math.Max(32, (int)Math.Round(pixels / 32.0) * 32);

    internal static double? ProjectHeight(IReadOnlyList<SKPointI> polygon, double score, double confidence,
        double sourceScaleX, double sourceScaleY)
    {
        if (polygon.Count != 4 || !double.IsFinite(score) || score * 100 < confidence)
        {
            return null;
        }

        double Length(int a, int b, double sx, double sy) => Math.Sqrt(
            Math.Pow((polygon[a].X - polygon[b].X) * sx, 2) + Math.Pow((polygon[a].Y - polygon[b].Y) * sy, 2));
        // Taking both pairs also handles vertical text and boxes whose starting edge changes at 90°.
        var shortSide = Math.Min(Math.Min(Length(0, 1, 1, 1), Length(2, 3, 1, 1)),
            Math.Min(Length(0, 3, 1, 1), Length(1, 2, 1, 1)));
        var longSide = Enumerable.Range(0, 4).Max(i => Length(i, (i + 1) % 4, 1, 1));
        if (shortSide < 6 || longSide / shortSide < 1.5)
        {
            return null;
        }

        var height = Enumerable.Range(0, 4).Min(i => Length(i, (i + 1) % 4, sourceScaleX, sourceScaleY));
        return double.IsFinite(height) && height > 3 ? height : null;
    }

    private static TextDetector Create(IConfiguration configuration, string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"PaddleOCR detector model is missing: {path}. Configure StorageOptimization:PaddleModelPath or select tesseract.");
        }

        var threads = configuration.GetValue<int?>("StorageOptimization:PaddleThreads") ?? 1;
        if (threads is < 1 or > 32)
        {
            throw new InvalidOperationException("StorageOptimization:PaddleThreads must be between 1 and 32.");
        }

        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Image shapes vary: retaining arena buffers across shapes grows a long-lived host's RSS.
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
        };
        var model = new TextDetector();
        try
        {
            model.InitModel(path, [0.485f * 255, 0.456f * 255, 0.406f * 255],
                [0.229f * 255, 0.224f * 255, 0.225f * 255], options);
            return model;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        detector?.Dispose();
    }
}
