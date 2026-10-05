namespace PaperDotNet.Ocr.Contracts;

public sealed record OcrResult(Stream Pdf, IReadOnlyList<string> PageTexts) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Pdf.DisposeAsync();
}

/// <summary>Recognizes ordered page images. Caller owns input streams; result owns its searchable PDF.</summary>
public interface IOcrService
{
    Task<OcrResult> RecognizeAsync(IReadOnlyList<Stream> pages, string languages, CancellationToken cancellationToken, bool allowEmpty = false);
}

/// <summary>Path-based recognition for host processing pipelines; caller owns input and output files.</summary>
public interface IOcrEngine
{
    Task<(string Pdf, IReadOnlyList<string> Pages)> RecognizeAsync(string input, string languages, string outputBase, CancellationToken ct, bool allowEmpty = false);
}

/// <summary>Projects detected text-region heights to the original image dimensions.</summary>
public interface ITextDetector
{
    string Model { get; }
    string? ModelDigest { get; }
    Task<IReadOnlyList<double>> DetectAsync(Stream image, int sourceWidth, int sourceHeight, double confidence, CancellationToken cancellationToken);
}

public interface IWordLayoutDetector
{
    Task<IReadOnlyList<double>> DetectAsync(Stream image, string languages, double scale, double confidence, CancellationToken cancellationToken);
}
