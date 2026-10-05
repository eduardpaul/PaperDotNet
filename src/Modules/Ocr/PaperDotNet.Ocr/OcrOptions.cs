namespace PaperDotNet.Ocr;

public sealed class OcrOptions
{
    public const string Section = "Ocr";
    /// <summary>The Tesseract executable (in the container image; <c>tesseract</c> on the path).</summary>
    public string TesseractPath { get; set; } = "tesseract";

    /// <summary>
    /// <c>tesseract</c> (default) or <c>glm</c>. <c>glm</c> calls an Ollama server running the
    /// <c>glm-ocr</c> model (<see cref="GlmBaseUrl"/>). The optional <c>Dockerfile.glm</c> image sets this.
    /// </summary>
    public string Engine { get; set; } = "tesseract";

    /// <summary>Ollama root address for <see cref="Engine"/> <c>glm</c> (no path; the client posts to <c>/api/generate</c>).</summary>
    public string GlmBaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>Ollama model name for <see cref="Engine"/> <c>glm</c>.</summary>
    public string GlmModel { get; set; } = "glm-ocr";

    /// <summary>
    /// Context length sent to GLM-OCR. A full-page photo uses several thousand tokens; too small a
    /// value truncates the image into garbage. 16384 fitted the receipt this was tried on and used about 5 GB.
    /// </summary>
    public int GlmContext { get; set; } = 16384;

    /// <summary>Maximum tokens GLM-OCR may write for one page.</summary>
    public int GlmMaxTokens { get; set; } = 8192;

    public TimeSpan OcrTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
