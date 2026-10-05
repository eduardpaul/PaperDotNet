using System.Text.Json.Nodes;

namespace PaperDotNet.Workflows.Contracts;

public sealed record ApprovalReviewReference(string Type, string Key);

public sealed record ApprovalReviewContext(Guid ApprovalId, Guid RunId, WorkflowItem Item, string Key);

public sealed record ApprovalReviewData(string Renderer, JsonObject Data, bool CanDecide, string? Reason = null);

public sealed record ApprovalReviewContent(Stream Content, string MediaType);

/// <summary>Registered review types are resolved within the tenant after assignment and item access checks.</summary>
public interface IApprovalReviewProvider
{
    string Type { get; }

    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    Task<ApprovalReviewData?> GetAsync(ApprovalReviewContext context, CancellationToken cancellationToken);

    Task<ApprovalReviewContent?> OpenAsync(ApprovalReviewContext context, string part, CancellationToken cancellationToken);

    Task<bool> CanDecideAsync(ApprovalReviewContext context, CancellationToken cancellationToken);
}
