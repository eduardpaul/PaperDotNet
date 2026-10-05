using System.Text.Json.Nodes;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.StorageOptimization;

internal sealed class OptimizationReview(IDocumentFileStore files) : IApprovalReviewProvider
{
    public string Type => StorageOptimizationExtension.ReviewType;

    private async Task<DocumentCandidate?> CandidateAsync(ApprovalReviewContext context, CancellationToken ct)
    {
        var candidate = Guid.TryParse(context.Key, out var id) ? await files.GetCandidateAsync(id, ct) : null;
        return candidate is not null && candidate.Source.ItemId == context.Item.ItemId
            && candidate.Source.WorkspaceId == context.Item.WorkspaceId && candidate.Source.ListId == context.Item.ListId
            && candidate.RunId == context.RunId ? candidate : null;
    }

    public async Task<ApprovalReviewData?> GetAsync(ApprovalReviewContext context, CancellationToken cancellationToken)
    {
        var candidate = await CandidateAsync(context, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var valid = await CanDecideAsync(context, cancellationToken);
        var data = candidate.Metrics.DeepClone().AsObject();
        data["sourceName"] = candidate.Source.FileName;
        data["sourceMediaType"] = candidate.Source.MediaType;
        data["name"] = candidate.FileName;
        data["mediaType"] = candidate.MediaType;
        data["state"] = candidate.State;
        return new("imageComparison", data, valid, valid ? null : "The review is settled or its source file changed.");
    }

    public async Task<ApprovalReviewContent?> OpenAsync(ApprovalReviewContext context, string part, CancellationToken cancellationToken)
    {
        var candidate = await CandidateAsync(context, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var content = part switch
        {
            "source" => await files.OpenVersionAsync(candidate.Source.Id, cancellationToken),
            "candidate" => await files.OpenCandidateAsync(candidate.Id, cancellationToken),
            _ => null,
        };
        return content is null ? null : new(content, part == "source" ? candidate.Source.MediaType : candidate.MediaType);
    }

    public async Task<bool> CanDecideAsync(ApprovalReviewContext context, CancellationToken cancellationToken)
    {
        var candidate = await CandidateAsync(context, cancellationToken);
        if (candidate is not { State: "pending" } || (await files.GetCurrentAsync(context.Item.ItemId, cancellationToken))?.Id != candidate.Source.Id)
        {
            return false;
        }

        await using var source = await files.OpenVersionAsync(candidate.Source.Id, cancellationToken);
        await using var content = await files.OpenCandidateAsync(candidate.Id, cancellationToken);
        return source is not null && content is not null;
    }
}
