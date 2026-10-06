namespace PaperDotNet.Documents.Contracts;

/// <summary>Renders an arbitrary PDF for extension previews without publishing it as a live file.</summary>
public interface IDocumentPdfRenderer
{
    Task<Stream?> RenderPageAsync(Stream pdf, int page, int width, CancellationToken cancellationToken);
}
