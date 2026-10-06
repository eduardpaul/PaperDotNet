using System.Text;
using SkiaSharp;

namespace PaperDotNet.Ocr;

/// <summary>Normalizes ordered JPEG, PNG and WebP pages without an external OCR executable.</summary>
internal static class OcrPageImages
{
    public static List<string> Read(string input, string directory, CancellationToken ct)
    {
        var images = new List<string>();
        ct.ThrowIfCancellationRequested();
        if (ReadPage(input, directory, images, ct)) return images;
        var lines = File.ReadAllLines(input, new UTF8Encoding(false, true)).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length is 0 or > 100 || lines.Any(line => !File.Exists(line)))
            throw new InvalidOperationException("The OCR input is neither an image nor an ordered list of 1–100 page images.");
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReadPage(line, directory, images, ct)) throw new InvalidOperationException("An OCR page is not a supported image.");
            if (images.Count > 100) throw new InvalidOperationException("OCR accepts at most 100 pages.");
        }
        return images;
    }

    private static bool ReadPage(string input, string directory, List<string> images, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var probe = File.OpenRead(input);
        Span<byte> header = stackalloc byte[4];
        var count = probe.Read(header);
        probe.Close();
        if (count == 4 && (header.SequenceEqual("II*\0"u8) || header.SequenceEqual("MM\0*"u8)))
        {
            throw new InvalidOperationException("TIFF decoding is not available for this engine. Convert to PNG/PDF or explicitly select Ocr:Engine=tesseract.");
        }
        using var codec = SKCodec.Create(input);
        if (codec is null) return false;
        Normalize(input, codec, directory, images, ct);
        return true;
    }

    private static void Normalize(string input, SKCodec codec, string directory, List<string> images, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckSize(codec.Info.Width, codec.Info.Height);
        if (codec.FrameCount > 1) throw new InvalidOperationException("Animated images cannot be recognized as document pages.");
        using var decoded = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidOperationException($"Cannot decode OCR page '{input}'.");
        var origin = codec.EncodedOrigin;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        using var upright = new SKBitmap(swap ? decoded.Height : decoded.Width, swap ? decoded.Width : decoded.Height);
        using var canvas = new SKCanvas(upright);
        var w = decoded.Width;
        var h = decoded.Height;
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        });
        canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Flush();
        images.Add(Save(upright, directory));
    }

    private static void CheckSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 64_000_000)
            throw new InvalidOperationException("An OCR page exceeds the 64-million-pixel decode limit or has invalid dimensions.");
    }

    private static string Save(SKBitmap bitmap, string directory)
    {
        var path = Path.Combine(directory, $"ocr-page-{Guid.NewGuid():N}.png");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
        return path;
    }
}
