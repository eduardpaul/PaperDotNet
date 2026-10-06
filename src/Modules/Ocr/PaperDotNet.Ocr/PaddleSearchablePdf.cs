using System.Globalization;
using System.IO.Compression;
using System.Text;
using PaperDotNet.Ocr.Contracts;
using SkiaSharp;

namespace PaperDotNet.Ocr;

internal sealed record PaddlePdfRegion(string Text, IReadOnlyList<SKPointI> Polygon);
internal sealed record PaddlePdfPage(string Image, string Text, IReadOnlyList<PaddlePdfRegion> Regions, OcrPageSize? Size = null);

/// <summary>Compressed page images and an invisible, positioned Unicode text layer. No system fonts are required.</summary>
internal static class PaddleSearchablePdf
{
    private const double PointsPerPixel = 72.0 / 150;

    public static void Write(IReadOnlyList<PaddlePdfPage> pages, string path, CancellationToken ct, int imageQuality = 80)
    {
        if (imageQuality is < 1 or > 100) throw new InvalidOperationException("Ocr:PaddlePdfImageQuality must be between 1 and 100.");
        using var pdf = new PdfObjects(path);
        var catalog = pdf.Reserve();
        var pageTree = pdf.Reserve();
        var characters = pages.SelectMany(p => p.Regions).SelectMany(r => r.Text.EnumerateRunes()).Distinct()
            .Select((rune, index) => (rune, code: index + 1)).ToDictionary(pair => pair.rune, pair => pair.code);
        if (characters.Count > ushort.MaxValue) throw new InvalidOperationException("Too many distinct characters in the OCR PDF.");
        var font = AddFont(pdf, characters);
        var pageIds = new List<int>(pages.Count);
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            using var codec = SKCodec.Create(page.Image) ?? throw new InvalidOperationException("Cannot decode the OCR PDF image.");
            using var bitmap = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new InvalidOperationException("Cannot decode the OCR PDF image.");
            var size = page.Size ?? new(bitmap.Width, bitmap.Height);
            if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 64_000_000)
                throw new InvalidOperationException("The PDF image size exceeds the 64-million-pixel limit or has invalid dimensions.");
            using var resized = size.Width == bitmap.Width && size.Height == bitmap.Height ? null
                : bitmap.Resize(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                    new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new InvalidOperationException("Cannot resize the OCR PDF image.");
            var visible = resized ?? bitmap;
            var width = visible.Width * PointsPerPixel;
            var height = visible.Height * PointsPerPixel;
            var image = AddImage(pdf, visible, imageQuality, ct);
            var content = new StringBuilder(FormattableString.Invariant($"q {width:F6} 0 0 {height:F6} 0 0 cm /Im0 Do Q\n"));
            foreach (var region in page.Regions) AppendText(content, region, height, characters,
                visible.Width / (double)bitmap.Width, visible.Height / (double)bitmap.Height);
            var operations = pdf.Stream("", Encoding.ASCII.GetBytes(content.ToString()));
            pageIds.Add(pdf.Add(FormattableString.Invariant($"<< /Type /Page /Parent {pageTree} 0 R /MediaBox [0 0 {width:F6} {height:F6}] /Resources << /XObject << /Im0 {image} 0 R >> /Font << /F0 {font} 0 R >> >> /Contents {operations} 0 R >>")));
        }
        pdf.Set(pageTree, $"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] >>");
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pageTree} 0 R >>");
        ct.ThrowIfCancellationRequested();
        pdf.Save(catalog);
    }

    private static int AddFont(PdfObjects pdf, Dictionary<Rune, int> characters)
    {
        // The invisible glyph has a one-em advance. ToUnicode retains every recognized Unicode scalar,
        // including supplementary characters, independently of the font's visual glyph coverage.
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "models", "OcrText.ttf"));
        var fontFile = pdf.Stream($"/Length1 {bytes.Length}", bytes);
        var descriptor = pdf.Add($"<< /Type /FontDescriptor /FontName /PaperDotNetOcrText /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 1000 /Descent 0 /CapHeight 1000 /StemV 80 /FontFile2 {fontFile} 0 R >>");
        var glyphMap = pdf.Stream("", new byte[(characters.Count + 1) * 2]);
        var descendant = pdf.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /PaperDotNetOcrText /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /DW 1000 /CIDToGIDMap {glyphMap} 0 R >>");
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /PaperDotNetUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in characters.Chunk(100))
        {
            cmap.Append(CultureInfo.InvariantCulture, $"{chunk.Length} beginbfchar\n");
            foreach (var (rune, code) in chunk)
                cmap.Append(CultureInfo.InvariantCulture, $"<{code:X4}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(rune.ToString()))}>\n");
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var unicode = pdf.Stream("", Encoding.ASCII.GetBytes(cmap.ToString()));
        return pdf.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /PaperDotNetOcrText /Encoding /Identity-H /DescendantFonts [{descendant} 0 R] /ToUnicode {unicode} 0 R >>");
    }

    private static void AppendText(StringBuilder content, PaddlePdfRegion region, double pageHeight, Dictionary<Rune, int> characters,
        double scaleX, double scaleY)
    {
        var runes = region.Text.EnumerateRunes().ToArray();
        if (runes.Length == 0 || region.Polygon.Count != 4) return;
        var p = region.Polygon;
        var a = (p[2].X - p[3].X) * PointsPerPixel * scaleX / runes.Length;
        var b = -(p[2].Y - p[3].Y) * PointsPerPixel * scaleY / runes.Length;
        var c = (p[0].X - p[3].X) * PointsPerPixel * scaleX;
        var d = -(p[0].Y - p[3].Y) * PointsPerPixel * scaleY;
        var x = p[3].X * PointsPerPixel * scaleX;
        var y = pageHeight - p[3].Y * PointsPerPixel * scaleY;
        content.Append(CultureInfo.InvariantCulture, $"BT /F0 1 Tf 3 Tr {a:F6} {b:F6} {c:F6} {d:F6} {x:F6} {y:F6} Tm <");
        foreach (var rune in runes) content.Append(characters[rune].ToString("X4", CultureInfo.InvariantCulture));
        content.Append("> Tj ET\n");
    }

    private static int AddImage(PdfObjects pdf, SKBitmap bitmap, int quality, CancellationToken ct)
    {
        // OCR reads the lossless normalized page. Only the PDF's visible image is compressed.
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawColor(SKColors.White, SKBlendMode.DstOver);
            canvas.Flush();
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidOperationException("Cannot encode the OCR PDF image.");
        ct.ThrowIfCancellationRequested();
        // Flat scans can be smaller losslessly. Compare on disk, without retaining a second page buffer.
        using var lossless = new FileStream(Path.Combine(Path.GetTempPath(), $"pdn_pdf_image_{Guid.NewGuid():N}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        ImageData(bitmap, lossless, ct);
        var dictionary = $"/Type /XObject /Subtype /Image /Width {bitmap.Width} /Height {bitmap.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8";
        if (lossless.Length < jpeg.Size)
        {
            lossless.Position = 0;
            return pdf.Stream(dictionary + " /Filter /FlateDecode", output => lossless.CopyTo(output));
        }
        return pdf.Stream(dictionary + " /Filter /DCTDecode", output => jpeg.SaveTo(output));
    }

    private static void ImageData(SKBitmap image, Stream output, CancellationToken ct)
    {
        using var compressed = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
        var pixels = image.GetPixelSpan();
        var row = new byte[image.Width * 3];
        for (var y = 0; y < image.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < image.Width; x++)
            {
                var offset = y * image.RowBytes + x * 4;
                var white = 255 - pixels[offset + 3];
                row[x * 3] = (byte)(pixels[offset + 2] + white);
                row[x * 3 + 1] = (byte)(pixels[offset + 1] + white);
                row[x * 3 + 2] = (byte)(pixels[offset] + white);
            }
            compressed.Write(row);
        }
    }

    private sealed class PdfObjects : IDisposable
    {
        private readonly FileStream file;
        private readonly List<long> offsets = [];

        public PdfObjects(string path)
        {
            file = File.Create(path);
            file.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
        }

        public int Reserve() { offsets.Add(0); return offsets.Count; }

        private void Start(int id)
        {
            offsets[id - 1] = file.Position;
            file.Write(Encoding.ASCII.GetBytes($"{id} 0 obj\n"));
        }

        public void Set(int id, string text)
        {
            Start(id);
            file.Write(Encoding.ASCII.GetBytes(text));
            file.Write("\nendobj\n"u8);
        }

        public int Add(string text) { var id = Reserve(); Set(id, text); return id; }

        public int Stream(string dictionary, byte[] data)
        {
            var id = Reserve();
            Start(id);
            file.Write(Encoding.ASCII.GetBytes($"<< {dictionary} /Length {data.Length} >>\nstream\n"));
            file.Write(data);
            file.Write("\nendstream\nendobj\n"u8);
            return id;
        }

        public int Stream(string dictionary, Action<Stream> write)
        {
            var id = Reserve();
            var length = Reserve();
            Start(id);
            file.Write(Encoding.ASCII.GetBytes($"<< {dictionary} /Length {length} 0 R >>\nstream\n"));
            var start = file.Position;
            write(file);
            var bytes = file.Position - start;
            file.Write("\nendstream\nendobj\n"u8);
            Set(length, bytes.ToString(CultureInfo.InvariantCulture));
            return id;
        }

        public void Save(int catalog)
        {
            var xref = file.Position;
            file.Write(Encoding.ASCII.GetBytes($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n"));
            foreach (var offset in offsets)
                file.Write(Encoding.ASCII.GetBytes(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"));
            file.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size {offsets.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        }

        public void Dispose() => file.Dispose();
    }
}
