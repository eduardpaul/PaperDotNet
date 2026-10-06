using System.Globalization;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using PaperDotNet.Ocr.Contracts;

namespace PaperDotNet.Ocr;

internal sealed class TesseractWordDetector(IOptions<OcrOptions> options) : IWordLayoutDetector
{
    public async Task<IReadOnlyList<double>> DetectAsync(Stream image, string languages, double scale, double confidence, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("pdn_layout_");
        try
        {
            var input = Path.Combine(work.FullName, "image.png");
            await using (var output = File.Create(input)) await image.CopyToAsync(output, ct);
            var outputBase = Path.Combine(work.FullName, "layout");
            var result = await Cli.Wrap(options.Value.TesseractPath).WithArguments([input, outputBase, "-l", languages, "tsv"])
                .WithEnvironmentVariables(e => e.Set("OMP_THREAD_LIMIT", "1"))
                .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(ct);
            if (result.ExitCode != 0 || !File.Exists(outputBase + ".tsv"))
                throw new InvalidOperationException($"Text analysis failed: {result.StandardError.Trim()[..Math.Min(500, result.StandardError.Trim().Length)]}");
            return WordHeights(await File.ReadAllTextAsync(outputBase + ".tsv", ct), scale, confidence);
        }
        finally { work.Delete(recursive: true); }
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

}
