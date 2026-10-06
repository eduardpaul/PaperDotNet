using Microsoft.Extensions.Configuration;
using Microsoft.ML.OnnxRuntime;
using PaperDotNet.Ocr.Contracts;
using RapidOcrNet;
using SkiaSharp;

namespace PaperDotNet.Ocr;

/// <summary>Lazy, process-wide offline PaddleOCR sessions with serialized CPU inference.</summary>
internal sealed class PaddleOcr(IConfiguration configuration) : IDisposable
{
    private PaddleModels? pipeline;

    public async Task<(string Pdf, IReadOnlyList<string> Pages)> RecognizeAsync(string input, string outputBase,
        CancellationToken cancellationToken, bool allowEmpty = false, IReadOnlyList<OcrPageSize>? pdfPageSizes = null)
    {
        await PaddleInferenceBudget.Gate.WaitAsync(cancellationToken);
        try
        {
            var work = Directory.CreateTempSubdirectory("pdn_paddle_pages_");
            try
            {
                return await Task.Run(() => Recognize(input, outputBase, work.FullName, allowEmpty, pdfPageSizes, cancellationToken), cancellationToken);
            }
            finally { work.Delete(recursive: true); }
        }
        finally { PaddleInferenceBudget.Gate.Release(); }
    }

    private (string Pdf, IReadOnlyList<string> Pages) Recognize(string input, string outputBase, string directory, bool allowEmpty,
        IReadOnlyList<OcrPageSize>? pdfPageSizes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var imageQuality = configuration.GetValue<int?>("Ocr:PaddlePdfImageQuality") ?? 80;
        if (imageQuality is < 1 or > 100) throw new InvalidOperationException("Ocr:PaddlePdfImageQuality must be between 1 and 100.");
        var images = OcrPageImages.Read(input, directory, ct);
        if (images.Count is 0 or > 100) throw new InvalidOperationException("Provide 1–100 page images for PaddleOCR.");
        if (pdfPageSizes is not null && pdfPageSizes.Count != images.Count)
            throw new ArgumentException("Provide one PDF image size per OCR page.");
        pipeline ??= Create();
        var pages = new List<PaddlePdfPage>(images.Count);
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            using var codec = SKCodec.Create(image) ?? throw new InvalidOperationException("Cannot decode a PaddleOCR page.");
            if ((long)codec.Info.Width * codec.Info.Height > 64_000_000)
                throw new InvalidOperationException("A PaddleOCR page exceeds the 64-million-pixel decode limit.");
            using var bgr = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (codec.GetPixels(bgr.Info, bgr.GetPixels()) != SKCodecResult.Success)
                throw new InvalidOperationException("Cannot decode a PaddleOCR page.");
            var texts = new List<string>();
            var regions = new List<PaddlePdfRegion>();
            // Detection uses a bounded preview; the polygons are mapped back to these original pixels.
            foreach (var box in pipeline.Detector.DetectBoxes(bgr, 50, ct))
            {
                ct.ThrowIfCancellationRequested();
                using var crop = PaddleTextCrop.Create(bgr, box.BoxPoints, out var polygon);
                var angle = pipeline.Classifier.GetAngle(crop, preserveAspectRatio: true, ct);
                using var upright = angle.Index == 1 && angle.Score >= 0.9f ? PaddleTextCrop.Rotate(crop, 180) : null;
                if (upright is not null) polygon = [polygon[2], polygon[3], polygon[0], polygon[1]];
                var line = pipeline.Recognizer.GetTextLine(upright ?? crop, ct);
                if (line.Chars is null || line.CharScores is null)
                    throw new InvalidOperationException("PaddleOCR text-line recognition failed.");
                if (line.CharScores.Length == 0 || line.CharScores.Average() < 0.5f) continue;
                var value = string.Concat(line.Chars);
                if (string.IsNullOrWhiteSpace(value)) continue;
                texts.Add(value);
                regions.AddRange(PaddleTextCrop.Regions(line, polygon));
            }
            ct.ThrowIfCancellationRequested();
            var text = string.Join('\n', texts).TrimEnd();
            if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("PaddleOCR returned no text.");
            pages.Add(new(image, text, regions, pdfPageSizes?[pages.Count]));
        }
        var pdf = outputBase + ".pdf";
        PaddleSearchablePdf.Write(pages, pdf, ct, imageQuality);
        return (pdf, pages.Select(page => page.Text).ToArray());
    }

    private PaddleModels Create()
    {
        string ModelPath(string key, string filename) => configuration[key] ?? Path.Combine(AppContext.BaseDirectory, "models", filename);
        var models = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = configuration["Ocr:PaddleModelPath"] ?? configuration["StorageOptimization:PaddleModelPath"]
                ?? Path.Combine(AppContext.BaseDirectory, "models", "PP-OCRv6_small_det.onnx"),
            RecModelPath = ModelPath("Ocr:PaddleRecognitionModelPath", "PP-OCRv6_small_rec.onnx"),
            ClsModelPath = ModelPath("Ocr:PaddleClassifierModelPath", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            KeysPath = ModelPath("Ocr:PaddleDictionaryPath", "ppocrv6_small_dict.txt"),
            // The official PaddlePaddle export uses ImageNet normalization, unlike RapidOCR's v6 exports.
            DetMean = [0.485f * 255, 0.456f * 255, 0.406f * 255],
            DetStd = [0.229f * 255, 0.224f * 255, 0.225f * 255],
        };
        foreach (var path in new[] { models.DetModelPath, models.RecModelPath, models.ClsModelPath, models.KeysPath })
            if (!File.Exists(path)) throw new InvalidOperationException($"PaddleOCR model or dictionary is missing: {path}.");
        var threads = configuration.GetValue<int?>("Ocr:PaddleThreads") ?? configuration.GetValue<int?>("StorageOptimization:PaddleThreads") ?? 1;
        if (threads is < 1 or > 32) throw new InvalidOperationException("Ocr:PaddleThreads must be between 1 and 32.");
        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
        };
        // Fail before inference if the supplied recognizer and dictionary cannot decode each other's classes.
        using (var recognizer = new InferenceSession(models.RecModelPath, options))
        {
            var classes = recognizer.OutputMetadata.Values.First().Dimensions[^1];
            if (classes != File.ReadLines(models.KeysPath).Count() + 2)
                throw new InvalidOperationException($"The PaddleOCR recognition model ({classes} classes) and character dictionary ({File.ReadLines(models.KeysPath).Count()} characters) do not match.");
        }
        var ocr = new PaddleModels(new(configuration), new(), new());
        try
        {
            ocr.Classifier.InitModel(models.ClsModelPath, options);
            ocr.Recognizer.InitModel(models.RecModelPath, models.KeysPath, options);
            return ocr;
        }
        catch { ocr.Dispose(); throw; }
    }

    private sealed record PaddleModels(PaddleTextDetector Detector, TextClassifier Classifier, TextRecognizer Recognizer) : IDisposable
    {
        public void Dispose() { Detector.Dispose(); Classifier.Dispose(); Recognizer.Dispose(); }
    }

    public void Dispose()
    {
        pipeline?.Dispose();
    }
}
