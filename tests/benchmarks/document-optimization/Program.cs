using System.Diagnostics;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Documents.Features.PhotoToDocument;
using PaperDotNet.Documents.Features.StorageOptimization;
using PaperDotNet.Ocr;
using PaperDotNet.Ocr.Contracts;
using SkiaSharp;
using UglyToad.PdfPig;

if (args is ["--render-pdf", var renderSource, var renderDestination])
{
    if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
    {
        PDFtoImage.Conversion.SavePng(renderDestination, File.ReadAllBytes(renderSource), 0,
            password: null, options: new PDFtoImage.RenderOptions { Dpi = 72 });
    }
    else throw new PlatformNotSupportedException();
    return;
}

if (args is ["--synthetic", var destination])
{
    using var bitmap = new SKBitmap(6000, 8000);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var typeface = SKTypeface.FromFile(Environment.GetEnvironmentVariable("PAPERDOTNET_BENCHMARK_FONT") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf");
    using var font = new SKFont(typeface, 240);
    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
    for (var i = 0; i < 20; i++)
    {
        canvas.DrawText("RECEIPT MILK 123.45 TOTAL", 100, 500 + i * 350, SKTextAlign.Left, font, paint);
    }

    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    using var output = File.Create(destination);
    png.SaveTo(output);
    output.SetLength(48L * 1024 * 1024);
    return;
}

if (args is ["--photo-to-pdf", var photo, var pdfDestination])
{
    var photoSettings = new ConfigurationBuilder().Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(photoSettings);
    new OcrModule().AddServices(services, photoSettings);
    using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var photoWork = Directory.CreateTempSubdirectory("pdn_photo_benchmark_");
    var timer = Stopwatch.StartNew();
    try
    {
        var normalized = Path.Combine(photoWork.FullName, "original.png");
        var analysis = new ImageTextAnalysis(photoSettings, scope.ServiceProvider.GetRequiredService<ITextDetector>(),
            scope.ServiceProvider.GetRequiredService<IWordLayoutDetector>());
        var size = await ComposePhotosActivity.PreparePageAsync(photo, normalized, "spa", analysis, CancellationToken.None);
        using var inputCodec = SKCodec.Create(normalized);
        await using var input = File.OpenRead(normalized);
        await using var result = await scope.ServiceProvider.GetRequiredService<IOcrService>()
            .RecognizeAsync([input], "spa", [size], CancellationToken.None);
        var pdfPath = Path.GetFullPath(pdfDestination);
        Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
        await using (var output = File.Create(pdfPath)) await result.Pdf.CopyToAsync(output);
        using var pdf = PdfDocument.Open(pdfPath);
        var page = pdf.GetPage(1);
        var embedded = page.GetImages().Single();
        if (embedded.WidthInSamples != size.Width || embedded.HeightInSamples != size.Height)
            throw new InvalidOperationException("The PDF did not use the independently requested image size.");
        if (page.Text.TrimEnd() != result.PageTexts.Single().TrimEnd())
            throw new InvalidOperationException("The PDF text differs from text recognized before resizing.");
        var sourceBytes = new FileInfo(photo).Length;
        var pdfBytes = new FileInfo(pdfPath).Length;
        var metrics = new
        {
            source = Path.GetFullPath(photo), sourceBytes, pdfBytes,
            savingsPercent = 100.0 * (sourceBytes - pdfBytes) / sourceBytes,
            ocrWidth = inputCodec.Info.Width, ocrHeight = inputCodec.Info.Height,
            pdfImageWidth = embedded.WidthInSamples, pdfImageHeight = embedded.HeightInSamples,
            recognizedCharacters = result.PageTexts.Single().Length,
            milliseconds = timer.ElapsedMilliseconds,
            hostPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
            pdfTextMatchesRecognition = true,
        };
        await File.WriteAllTextAsync(Path.ChangeExtension(pdfPath, ".txt"), result.PageTexts.Single());
        await File.WriteAllTextAsync(Path.ChangeExtension(pdfPath, ".json"), JsonSerializer.Serialize(metrics, BenchmarkJson.Options) + "\n");
        Console.WriteLine(JsonSerializer.Serialize(metrics, BenchmarkJson.Options));
    }
    finally { photoWork.Delete(recursive: true); }
    return;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: OptimizationBenchmark <image or directory> <results.json>; --synthetic <48MiB.png>; --photo-to-pdf <photo> <output.pdf>; or --render-pdf <pdf> <page.png>");
    return;
}

using var gate = new ImageOptimizationGate();
var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Ocr:TesseractPath"] = Environment.GetEnvironmentVariable("TESSERACT_PATH") ?? "tesseract",
    ["StorageOptimization:TextDetector"] = Environment.GetEnvironmentVariable("TEXT_DETECTOR") ?? "paddleocr",
}).Build();
using var detector = new PaddleTextDetector(settings);
var adapter = new ImageOptimizationAdapter(settings, gate, detector, new TesseractWordDetector(Microsoft.Extensions.Options.Options.Create(new OcrOptions { TesseractPath = settings["Ocr:TesseractPath"]! })));
var sources = File.Exists(args[0]) ? [args[0]] : Directory.GetFiles(args[0]).Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant())).Order().ToArray();
var records = new List<object>();
var work = Directory.CreateTempSubdirectory("pdn_verify_");
try
{
    foreach (var path in sources)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await using var content = File.OpenRead(path);
            await using var result = await adapter.OptimizeAsync(content, "eng", new DocumentOptimizationOptions(), CancellationToken.None);
            int? verifiedWords = null;
            double? verifiedHeight = null;
            if (result.Content is { } optimized)
            {
                var imagePath = Path.Combine(work.FullName, "optimized.webp");
                await using (var file = File.Create(imagePath))
                {
                    await optimized.CopyToAsync(file);
                }

                var outputBase = Path.Combine(work.FullName, "verify");
                await Cli.Wrap(settings["Ocr:TesseractPath"]!).WithArguments([imagePath, outputBase, "-l", "eng", "tsv"])
                    .WithEnvironmentVariables(e => e.Set("OMP_THREAD_LIMIT", "1")).ExecuteBufferedAsync();
                var heights = TesseractWordDetector.WordHeights(await File.ReadAllTextAsync(outputBase + ".tsv"), 1, 50);
                verifiedWords = heights.Count;
                verifiedHeight = heights.Count > 0 ? ImageOptimizationAdapter.Percentile(heights, 5) : null;
            }

            records.Add(new { file = Path.GetFileName(path), metrics = result.Metrics, result.SkipReason, milliseconds = timer.ElapsedMilliseconds,
                hostPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64, verifiedWords, verifiedHeight });
            Console.WriteLine($"{Path.GetFileName(path)}: {timer.ElapsedMilliseconds}ms; {result.SkipReason ?? result.Metrics["savingsPercent"]?.ToString() + "% saved"}");
        }
        catch (Exception ex)
        {
            records.Add(new { file = Path.GetFileName(path), error = ex.Message });
            Console.WriteLine($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(records, BenchmarkJson.Options) + "\n");
}
finally
{
    work.Delete(recursive: true);
}

internal static class BenchmarkJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
