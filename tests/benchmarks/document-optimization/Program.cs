using System.Diagnostics;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.StorageOptimization;
using SkiaSharp;

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

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: OptimizationBenchmark <image or directory> <results.json>; or --synthetic <48MiB.png>");
    return;
}

using var gate = new ImageOptimizationGate();
var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Documents:TesseractPath"] = Environment.GetEnvironmentVariable("TESSERACT_PATH") ?? "tesseract",
    ["StorageOptimization:TextDetector"] = Environment.GetEnvironmentVariable("TEXT_DETECTOR") ?? "paddleocr",
}).Build();
using var detector = new PaddleTextDetector(settings);
var adapter = new ImageOptimizationAdapter(settings, gate, detector);
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
                await Cli.Wrap(settings["Documents:TesseractPath"]!).WithArguments([imagePath, outputBase, "-l", "eng", "tsv"])
                    .WithEnvironmentVariables(e => e.Set("OMP_THREAD_LIMIT", "1")).ExecuteBufferedAsync();
                var heights = ImageOptimizationAdapter.WordHeights(await File.ReadAllTextAsync(outputBase + ".tsv"), 1, 50);
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
