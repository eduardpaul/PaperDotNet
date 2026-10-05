using PaperDotNet.Ocr.Contracts;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Ocr;

internal sealed partial class ConfiguredOcrService(OcrEngine engine) : IOcrService
{
    public async Task<OcrResult> RecognizeAsync(IReadOnlyList<Stream> pages, string languages,
        CancellationToken cancellationToken, bool allowEmpty = false)
    {
        if (pages.Count is 0 or > 100 || !LanguageList().IsMatch(languages))
            throw new ArgumentException("Provide 1–100 ordered page images and valid OCR languages.");
        var work = Directory.CreateTempSubdirectory("pdn_ocr_pages_");
        string? resultPath = null;
        try
        {
            var paths = new List<string>();
            for (var i = 0; i < pages.Count; i++)
            {
                var path = Path.Combine(work.FullName, $"page-{i}.png");
                await using (var output = File.Create(path)) await pages[i].CopyToAsync(output, cancellationToken);
                paths.Add(path);
            }
            var list = Path.Combine(work.FullName, "pages.txt");
            await File.WriteAllLinesAsync(list, paths, cancellationToken);
            var (pdf, text) = await engine.RecognizeAsync(list, languages, Path.Combine(work.FullName, "result"), cancellationToken, allowEmpty);
            resultPath = Path.Combine(Path.GetTempPath(), $"pdn_ocr_result_{Ids.New():N}.pdf");
            File.Move(pdf, resultPath);
            var content = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            resultPath = null;
            return new(content, text);
        }
        finally
        {
            if (resultPath is not null) File.Delete(resultPath);
            work.Delete(recursive: true);
        }
    }
    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z_]{1,30}(\\+[a-z][a-z_]{1,30}){0,5}$")]
    private static partial System.Text.RegularExpressions.Regex LanguageList();
}

