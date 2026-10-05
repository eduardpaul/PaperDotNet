using RapidOcrNet;
using SkiaSharp;

namespace PaperDotNet.Ocr;

/// <summary>Rectifies one text line directly from original pixels, never from the detection preview.</summary>
internal static class PaddleTextCrop
{
    internal static SKBitmap Create(SKBitmap original, IReadOnlyList<SKPointI> points, out SKPointI[] readingOrder)
    {
        if (points.Count != 4) throw new InvalidOperationException("A text crop needs four corners.");
        double Edge(int a, int b) => Math.Sqrt(Math.Pow(points[a].X - points[b].X, 2) + Math.Pow(points[a].Y - points[b].Y, 2));
        var width = Math.Max(1, (int)Math.Round(Math.Max(Edge(0, 1), Edge(2, 3))));
        var height = Math.Max(1, (int)Math.Round(Math.Max(Edge(0, 3), Edge(1, 2))));
        if ((long)width * height > 16_000_000 || Math.Max(width, height) / (double)Math.Min(width, height) > 128)
            throw new InvalidOperationException("A text line exceeds the PaddleOCR recognition memory bound. Crop or split this page before OCR.");
        var transform = RectangleToQuad(points, width, height);
        if (!transform.TryInvert(out var inverse)) throw new InvalidOperationException("A detected text polygon cannot be rectified.");
        var crop = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            using (var canvas = new SKCanvas(crop))
            {
                canvas.Clear(SKColors.White);
                canvas.SetMatrix(inverse);
                canvas.DrawBitmap(original, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
                canvas.Flush();
            }
            readingOrder = points.ToArray();
            if (height < width * 1.5) return crop;
            var rotated = Rotate(crop, 90);
            crop.Dispose();
            readingOrder = [points[3], points[0], points[1], points[2]];
            return rotated;
        }
        catch { crop.Dispose(); throw; }
    }

    internal static SKBitmap Rotate(SKBitmap source, int angle)
    {
        var rotated = new SKBitmap(angle == 90 ? source.Height : source.Width,
            angle == 90 ? source.Width : source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            using var canvas = new SKCanvas(rotated);
            canvas.Clear(SKColors.White);
            if (angle == 90) { canvas.Translate(source.Height, 0); canvas.RotateDegrees(90); }
            else canvas.RotateDegrees(180, source.Width / 2f, source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            canvas.Flush();
            return rotated;
        }
        catch { rotated.Dispose(); throw; }
    }

    private static SKMatrix RectangleToQuad(IReadOnlyList<SKPointI> p, int width, int height)
    {
        double dx1 = p[1].X - p[2].X, dx2 = p[3].X - p[2].X, dx3 = p[0].X - p[1].X + p[2].X - p[3].X;
        double dy1 = p[1].Y - p[2].Y, dy2 = p[3].Y - p[2].Y, dy3 = p[0].Y - p[1].Y + p[2].Y - p[3].Y;
        double g = 0, h = 0;
        if (dx3 != 0 || dy3 != 0)
        {
            var denominator = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(denominator) < 1e-9) throw new InvalidOperationException("A detected text polygon is degenerate.");
            g = (dx3 * dy2 - dx2 * dy3) / denominator;
            h = (dx1 * dy3 - dx3 * dy1) / denominator;
        }
        return new SKMatrix(
            (float)((p[1].X - p[0].X + g * p[1].X) / width), (float)((p[3].X - p[0].X + h * p[3].X) / height), p[0].X,
            (float)((p[1].Y - p[0].Y + g * p[1].Y) / width), (float)((p[3].Y - p[0].Y + h * p[3].Y) / height), p[0].Y,
            (float)(g / width), (float)(h / height), 1);
    }

    internal static IReadOnlyList<PaddlePdfRegion> Regions(TextLine line, SKPointI[] polygon)
    {
        var chars = line.Chars ?? [];
        if (chars.Length == 0) return [];
        if (line.CharCols is not { } columns || columns.Length != chars.Length || line.ColCount <= 0)
            return [new(string.Concat(chars) + "\n", polygon)];
        var words = new List<(int Start, int End)>();
        for (var i = 0; i < chars.Length;)
        {
            if (string.IsNullOrWhiteSpace(chars[i])) { i++; continue; }
            var start = i++;
            if (!IsCjk(chars[start]))
                while (i < chars.Length && !string.IsNullOrWhiteSpace(chars[i]) && !IsCjk(chars[i])) i++;
            words.Add((start, i));
        }
        var regions = new List<PaddlePdfRegion>(words.Count);
        var quad = RectangleToQuad(polygon, 1, 1);
        for (var i = 0; i < words.Count; i++)
        {
            var (start, end) = words[i];
            var left = Math.Clamp((columns[start] - 0.5f) / line.ColCount, 0, 1);
            var right = Math.Clamp((columns[end - 1] + 1.5f) / line.ColCount, left, 1);
            SKPointI Map(float x, float y)
            {
                var point = quad.MapPoint(x, y);
                return new((int)Math.Round(point.X), (int)Math.Round(point.Y));
            }
            var text = string.Concat(chars[(i == 0 ? 0 : start)..(i + 1 < words.Count ? words[i + 1].Start : chars.Length)]);
            regions.Add(new(text + (i + 1 == words.Count ? "\n" : ""), [Map(left, 0), Map(right, 0), Map(right, 1), Map(left, 1)]));
        }
        return regions;
    }

    private static bool IsCjk(string text)
    {
        var code = char.ConvertToUtf32(text, 0);
        return code is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3134F;
    }
}
