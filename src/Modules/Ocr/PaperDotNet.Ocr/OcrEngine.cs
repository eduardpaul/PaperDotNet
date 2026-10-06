using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using PaperDotNet.Ocr.Contracts;

namespace PaperDotNet.Ocr;

internal sealed class OcrEngine(IOptions<OcrOptions> options, IHttpClientFactory http, PaddleOcr paddle) : IOcrEngine
{
    public Task<(string Pdf, IReadOnlyList<string> Pages)> RecognizeAsync(string input, string languages, string outputBase, CancellationToken ct, bool allowEmpty = false) =>
        RecognizeAsync(input, languages, outputBase, null, ct, allowEmpty);

    internal async Task<(string Pdf, IReadOnlyList<string> Pages)> RecognizeAsync(string input, string languages, string outputBase,
        IReadOnlyList<OcrPageSize>? pdfPageSizes, CancellationToken ct, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Engine) || options.Value.Engine.Trim().Equals("paddleocr", StringComparison.OrdinalIgnoreCase))
        {
            using var paddleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            paddleTimeout.CancelAfter(options.Value.OcrTimeout);
            return await paddle.RecognizeAsync(input, outputBase, paddleTimeout.Token, allowEmpty, pdfPageSizes);
        }

        if (GlmOcr.Uses(options.Value.Engine))
        {
            var recognized = await new GlmOcr(options.Value, http.CreateClient(GlmOcr.HttpClientName)).RecognizeAsync(input, outputBase, ct, allowEmpty);
            if (pdfPageSizes is not null) OcrPdfImages.Resize(recognized.Pdf, input, pdfPageSizes, ct);
            return recognized;
        }

        if (!UsesTesseract(options.Value.Engine))
        {
            throw new InvalidOperationException($"Unknown OCR engine '{options.Value.Engine}'. Use \"paddleocr\", \"tesseract\", or \"glm\".");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.OcrTimeout);
        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(options.Value.TesseractPath)
                .WithArguments([input, outputBase, "-l", languages, "pdf", "txt"])
                .WithEnvironmentVariables(e => e.Set("OMP_THREAD_LIMIT", "1"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(timeout.Token);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"The OCR engine '{options.Value.TesseractPath}' is not installed.", ex);
        }

        var pdf = outputBase + ".pdf";
        if (result.ExitCode != 0 || !File.Exists(pdf))
        {
            var error = result.StandardError.Trim();
            throw new InvalidOperationException($"OCR failed: {(error.Length > 500 ? error[..500] : error)}");
        }

        var text = await File.ReadAllTextAsync(outputBase + ".txt", Encoding.UTF8, ct);
        var pages = text.Split('\f').ToList();
        if (pages.Count > 1 && string.IsNullOrWhiteSpace(pages[^1]))
        {
            pages.RemoveAt(pages.Count - 1); // Tesseract ends every page with a form feed.
        }

        if (pdfPageSizes is not null) OcrPdfImages.Resize(pdf, input, pdfPageSizes, ct);
        return (pdf, pages);
    }

    private static bool UsesTesseract(string? engine) =>
        string.IsNullOrWhiteSpace(engine) || engine.Trim().Equals("tesseract", StringComparison.OrdinalIgnoreCase);
}
