using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using System.Text.Json.Nodes;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.StorageOptimization;

internal sealed class CompositionReview(PhotoConversionStore compositions, IDocumentFileStore files, PaperDotNet.Lists.Contracts.IListItemStore items,
    ITenantScopeFactory scopes, ITenantContext tenant, IDocumentPdfRenderer renderer) : IApprovalReviewProvider
{
    private async Task<Stream?> OpenCandidateAsync(PhotoConversion selection, CancellationToken ct)
    {
        await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!, selection.StartedBy);
        return await scope.ServiceProvider.GetRequiredService<PhotoConversionStore>().OpenAsync(selection.Id, ct);
    }

    public string Type => PhotoToDocument.ReviewType;
    private async Task<PhotoConversion?> GetSelectionAsync(ApprovalReviewContext context, CancellationToken ct)
    {
        var selection = Guid.TryParse(context.Key, out var id) ? await compositions.GetAsync(id, ct) : null;
        if (selection is not null)
        {
            foreach (var source in selection.Sources)
                if (await items.GetAsync(source.File.WorkspaceId, source.File.ListId, source.File.ItemId, ct) is null) return null;
        }
        return selection is not null && selection.RunId == context.RunId && selection.PrimaryItemId == context.Item.ItemId
            && selection.Sources.All(s => s.File.WorkspaceId == context.Item.WorkspaceId && s.File.ListId == context.Item.ListId) ? selection : null;
    }

    public async Task<ApprovalReviewData?> GetAsync(ApprovalReviewContext context, CancellationToken cancellationToken)
    {
        var selection = await GetSelectionAsync(context, cancellationToken);
        if (selection is null) return null;
        var fresh = await CanDecideAsync(context, cancellationToken);
        var primary = selection.Sources.Single(s => s.File.ItemId == selection.PrimaryItemId);
        return new("pdfComposition", new JsonObject { ["name"] = selection.FileName, ["primaryName"] = primary.File.FileName,
            ["pageCount"] = selection.Sources.Count,
            ["description"] = $"Approving replaces {primary.File.FileName} with {selection.FileName} ({selection.Sources.Count} pages). The other {selection.Sources.Count - 1} photos move to the recycle bin.",
            ["approvedLabel"] = "Replace photos with PDF", ["rejectedLabel"] = "Keep original photos", ["sources"] = new JsonArray([.. selection.Sources.Select(s => (JsonNode)new JsonObject {
                ["name"] = s.File.FileName, ["mediaType"] = s.File.MediaType, ["itemId"] = s.File.ItemId.ToString() })]) }, fresh,
            fresh ? null : "A source changed, access was removed, or the review is settled. Launch a new conversion if needed.");
    }

    public async Task<bool> CanDecideAsync(ApprovalReviewContext context, CancellationToken cancellationToken)
    {
        var selection = await GetSelectionAsync(context, cancellationToken);
        if (selection is not { State: "pending" } || !await compositions.IsFreshAsync(selection.Id, false, cancellationToken)) return false;
        await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!, selection.StartedBy);
        if (!await scope.ServiceProvider.GetRequiredService<PhotoConversionStore>().IsFreshAsync(selection.Id, true, cancellationToken)) return false;
        await using var pdf = await OpenCandidateAsync(selection, cancellationToken);
        if (pdf is null) return false;
        foreach (var source in selection.Sources)
        {
            await using var image = await files.OpenVersionAsync(source.File.Id, cancellationToken);
            if (image is null) return false;
        }
        return true;
    }

    public async Task<ApprovalReviewContent?> OpenAsync(ApprovalReviewContext context, string part, CancellationToken cancellationToken)
    {
        var selection = await GetSelectionAsync(context, cancellationToken);
        if (selection is null || !await compositions.IsFreshAsync(selection.Id, false, cancellationToken)) return null;
        if (part == "candidate")
        {
            var pdf = await OpenCandidateAsync(selection, cancellationToken);
            return pdf is null ? null : new(pdf, "application/pdf");
        }
        if (part.StartsWith("page-", StringComparison.Ordinal) && int.TryParse(part.AsSpan(5), out var page) && page >= 1 && page <= selection.Sources.Count)
        {
            await using var pdf = await OpenCandidateAsync(selection, cancellationToken);
            var rendered = pdf is null ? null : await renderer.RenderPageAsync(pdf, page, 1600, cancellationToken);
            return rendered is null ? null : new(rendered, "image/jpeg");
        }
        if (!part.StartsWith("source-", StringComparison.Ordinal) || !int.TryParse(part.AsSpan(7), out var index) || index < 0 || index >= selection.Sources.Count) return null;
        var source = selection.Sources[index].File;
        var content = await files.OpenVersionAsync(source.Id, cancellationToken);
        return content is null ? null : new(content, source.MediaType);
    }
}
