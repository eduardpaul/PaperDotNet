using Microsoft.EntityFrameworkCore;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Documents.Features.PhotoToDocument;

internal sealed class PhotoConversionRetention(PhotoConversionDbContext db, IWorkflowDirectory workflows) : IStagedDocumentRetention
{
    public string Owner => PhotoToDocument.Key;
    public async Task<bool> IsRetainedAsync(Guid id, CancellationToken ct)
    {
        var record = await db.Conversions.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (record is null || record.State is not ("pending" or "preparing")) return false;
        if (await workflows.IsRunActiveAsync(record.RunId, ct)) return true;
        record.State = "abandoned";
        await db.SaveChangesAsync(ct);
        return false;
    }
}
