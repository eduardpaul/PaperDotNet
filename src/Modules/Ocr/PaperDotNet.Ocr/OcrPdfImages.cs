using PaperDotNet.Ocr.Contracts;
using SkiaSharp;
using UglyToad.PdfPig;

namespace PaperDotNet.Ocr;

/// <summary>Alternative engines also recognize originals before rebuilding with resized PDF images.</summary>
internal static class OcrPdfImages
{
    internal static void Resize(string pdf, string input, IReadOnlyList<OcrPageSize> sizes, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("pdn_pdf_resize_");
        try
        {
            var images = OcrPageImages.Read(input, work.FullName, ct);
            var pages = new List<PaddlePdfPage>(images.Count);
            using (var recognized = PdfDocument.Open(pdf))
            {
                if (images.Count != sizes.Count || recognized.NumberOfPages != images.Count)
                    throw new InvalidOperationException("OCR must produce one PDF page per image.");
                for (var i = 0; i < images.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var codec = SKCodec.Create(images[i]) ?? throw new InvalidOperationException("Cannot decode an OCR PDF page.");
                    var page = recognized.GetPage(i + 1);
                    SKPointI Point(UglyToad.PdfPig.Core.PdfPoint point) => new(
                        (int)Math.Round(point.X * codec.Info.Width / page.Width),
                        (int)Math.Round((page.Height - point.Y) * codec.Info.Height / page.Height));
                    var regions = page.Letters.Select(letter => new PaddlePdfRegion(letter.Value,
                        [Point(letter.BoundingBox.TopLeft), Point(letter.BoundingBox.TopRight),
                            Point(letter.BoundingBox.BottomRight), Point(letter.BoundingBox.BottomLeft)])).ToArray();
                    pages.Add(new(images[i], page.Text, regions, sizes[i]));
                }
            }
            var resized = Path.Combine(work.FullName, "resized.pdf");
            PaddleSearchablePdf.Write(pages, resized, ct);
            File.Move(resized, pdf, overwrite: true);
        }
        finally { work.Delete(recursive: true); }
    }
}
