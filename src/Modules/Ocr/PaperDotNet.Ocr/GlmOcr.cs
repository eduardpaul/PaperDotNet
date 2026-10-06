using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.Ocr;

/// <summary>
/// GLM-OCR through Ollama's native generate API (ADR-0034). One request per page image, prompt
/// <c>Text Recognition:</c>. The model returns plain text; this writes the searchable PDF the rest
/// of processing stores. Tesseract language codes are not sent: the model is multilingual, and the
/// caller still records the library language for stemming.
/// </summary>
internal sealed class GlmOcr(OcrOptions options, HttpClient http)
{
    public const string HttpClientName = "glm-ocr";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool Uses(string? engine) =>
        engine is not null && engine.Trim().Equals("glm", StringComparison.OrdinalIgnoreCase);

    public async Task<(string Pdf, IReadOnlyList<string> Pages)> RecognizeAsync(string input, string outputBase, CancellationToken ct, bool allowEmpty = false)
    {
        var work = Directory.CreateTempSubdirectory("pdn_glm_pages_");
        try
        {
            var images = OcrPageImages.Read(input, work.FullName, ct);
            var pages = new List<(string Image, string Text)>(images.Count);
            foreach (var image in images)
            {
                pages.Add((image, await RecognizePageAsync(image, allowEmpty, ct)));
            }

            var pdf = outputBase + ".pdf";
            SearchablePdf.Write(pages, pdf);
            return (pdf, pages.Select(page => page.Text).ToList());
        }
        finally { work.Delete(recursive: true); }
    }

    private async Task<string> RecognizePageAsync(string image, bool allowEmpty, CancellationToken ct)
    {
        var payload = new GenerateRequest(
            options.GlmModel,
            "Text Recognition:",
            [Convert.ToBase64String(await File.ReadAllBytesAsync(image, ct))],
            false,
            new GenerateOptions(0, options.GlmContext, options.GlmMaxTokens));
        using var response = await http.PostAsJsonAsync(Endpoint(options.GlmBaseUrl), payload, Json, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Length > 500 ? body[..500] : body;
            throw new InvalidOperationException($"GLM-OCR failed ({(int)response.StatusCode}): {detail}");
        }

        var parsed = JsonSerializer.Deserialize<GenerateResponse>(body, Json);
        if (string.Equals(parsed?.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "GLM-OCR stopped because the context or the output limit was too small. Raise Ocr:GlmContext or Ocr:GlmMaxTokens.");
        }

        var text = Clean(parsed?.Response);
        if (parsed?.Response is null || !allowEmpty && text.Length == 0)
        {
            throw new InvalidOperationException("GLM-OCR returned no text.");
        }

        return text;
    }

    /// <summary>Ollama root, for example <c>http://127.0.0.1:11434</c>, plus <c>/api/generate</c>.</summary>
    internal static Uri Endpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"Ocr:GlmBaseUrl '{baseUrl}' is not an absolute HTTP address.");
        }

        return new Uri(root, "api/generate");
    }

    /// <summary>Drops a single markdown fence some builds wrap around the page.</summary>
    internal static string Clean(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var newline = trimmed.IndexOf('\n');
        if (newline < 0 || !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return trimmed[(newline + 1)..^3].Trim();
    }

    private sealed record GenerateRequest(string Model, string Prompt, string[] Images, bool Stream, GenerateOptions Options);

    private sealed record GenerateOptions(
        double Temperature,
        [property: JsonPropertyName("num_ctx")] int NumCtx,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    private sealed record GenerateResponse(string? Response, [property: JsonPropertyName("done_reason")] string? DoneReason);
}

/// <summary>
/// A PDF whose pages are the scan and whose text layer is the recognition, drawn with rendering mode
/// "neither" so it is invisible and still extractable (the same shape Tesseract's PDF output has).
/// Helvetica only draws ASCII. Accents are stripped for the layer and other characters are left out.
/// Search uses the original page text, which is stored separately.
/// </summary>
internal static class SearchablePdf
{
    public static void Write(IReadOnlyList<(string Image, string Text)> pages, string path)
    {
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("OCR produced no pages.");
        }

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var (image, text) in pages)
        {
            var (width, height) = PagePoints(image);
            var page = builder.AddPage(width, height);
            DrawImage(page, image, width, height);
            page.SetTextRenderingMode(TextRenderingMode.Neither);
            DrawText(page, font, text, width, height);
        }

        File.WriteAllBytes(path, builder.Build());
    }

    private static (double Width, double Height) PagePoints(string image)
    {
        using var codec = SKCodec.Create(image) ?? throw new InvalidOperationException($"Cannot read page image '{image}'.");
        // 150 dpi keeps a phone photo of a receipt on a long but ordinary page.
        return (Math.Max(1, codec.Info.Width) * 72.0 / 150, Math.Max(1, codec.Info.Height) * 72.0 / 150);
    }

    private static void DrawImage(PdfPageBuilder page, string image, double width, double height)
    {
        var bytes = File.ReadAllBytes(image);
        var area = new PdfRectangle(0, 0, width, height);
        if (IsPng(bytes))
        {
            page.AddPng(bytes, area);
        }
        else if (IsJpeg(bytes))
        {
            page.AddJpeg(bytes, area);
        }
        else
        {
            throw new InvalidOperationException($"Page image '{image}' is neither PNG nor JPEG.");
        }
    }

    private static void DrawText(PdfPageBuilder page, PdfDocumentBuilder.AddedFont font, string text, double width, double height)
    {
        const double fontSize = 10;
        var margin = Math.Min(24, width / 10);
        var maxChars = Math.Max(8, (int)((width - (2 * margin)) / (fontSize * 0.5)));
        var y = height - margin - fontSize;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = ForStandardFont(raw);
            if (line.Length == 0)
            {
                y -= fontSize + 2;
                continue;
            }

            for (var i = 0; i < line.Length; i += maxChars)
            {
                page.AddText(line.Substring(i, Math.Min(maxChars, line.Length - i)), fontSize, new PdfPoint(margin, y), font);
                y -= fontSize + 2;
            }
        }
    }

    /// <summary>ASCII, after accents are removed. The standard PDF fonts have no other glyphs.</summary>
    internal static string ForStandardFont(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (character is >= ' ' and <= '~')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsPng(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;

    private static bool IsJpeg(ReadOnlySpan<byte> header) => header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8;
}
