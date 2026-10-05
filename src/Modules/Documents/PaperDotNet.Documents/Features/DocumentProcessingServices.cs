using PaperDotNet.Documents.Contracts;

namespace PaperDotNet.Documents.Features;

internal sealed class DocumentPdfRenderer : IDocumentPdfRenderer
{
    public async Task<Stream?> RenderPageAsync(Stream pdf, int page, int width, CancellationToken cancellationToken)
    {
        if (!PageRenderer.Widths.Contains(width)) throw new ArgumentOutOfRangeException(nameof(width), "Use a supported document preview width.");
        var rendered = await PageRenderer.RenderPdfPageAsync(pdf, page, width, cancellationToken);
        return rendered is null ? null : new MemoryStream(rendered, writable: false);
    }
}
