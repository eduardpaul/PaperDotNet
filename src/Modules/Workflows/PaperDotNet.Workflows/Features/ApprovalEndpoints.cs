using Microsoft.AspNetCore.Http.HttpResults;
using PaperDotNet.Api;
using PaperDotNet.Workflows.Data;

namespace PaperDotNet.Workflows.Features;

public sealed record ApprovalDto(
    Guid Id,
    Guid RunId,
    string Node,
    Guid WorkspaceId,
    Guid? ListId,
    Guid? ItemId,
    string Title,
    string Status,
    bool Escalated,
    DateTimeOffset? DueAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Comment,
    DateTimeOffset CreatedAt);

public sealed record ApprovalDecisionRequest(string Outcome, string? Comment);

/// <summary>
/// The caller's approvals (<c>/v1.0/me/approvals</c>): requests of approval nodes they are an assignee of, and their
/// decision (<c>approved</c> or <c>rejected</c>), which resumes the run.
/// </summary>
internal static class ApprovalEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/me/approvals").WithTags("Workflows");
        group.MapGet("", ListAsync).RequireScope(WorkflowScopes.Read).WithName("ListMyApprovals")
            .WithDescription("Approvals the caller is an assignee of, newest first; status=pending|approved|rejected|cancelled filters them.");
        group.MapPost("/{approvalId:guid}/decision", DecideAsync).RequireScope(WorkflowScopes.Read).WithName("DecideApproval");
    }

    private static async Task<Results<Ok<Page<ApprovalDto>>, ValidationProblem>> ListAsync(string? status, Caller caller, WorkflowsDbContext db, CancellationToken cancellationToken)
    {
        var wanted = status?.ToLowerInvariant();
        if (wanted is not null && !ApprovalStatus.All.Contains(wanted))
        {
            return ApiErrors.Validation("status", $"status must be one of: {string.Join(", ", ApprovalStatus.All)}.");
        }

        var approvals = wanted is null
            ? await WaitQueries.AllOfAssigneeAsync(db, caller.TenantId, caller.UserId, cancellationToken)
            : await WaitQueries.OfAssigneeAsync(db, caller.TenantId, caller.UserId, wanted, cancellationToken);
        return TypedResults.Ok(new Page<ApprovalDto>([.. approvals.Select(ToDto)], null));
    }

    private static async Task<Results<Ok<ApprovalDto>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        Guid approvalId, ApprovalDecisionRequest request, Caller caller, RunService runs, WorkflowsDbContext db, CancellationToken cancellationToken)
    {
        if (request.Outcome is not (ApprovalOutcomes.Approved or ApprovalOutcomes.Rejected))
        {
            return ApiErrors.Validation("outcome", "outcome must be approved or rejected.");
        }

        if (request.Comment?.Length > 2000)
        {
            return ApiErrors.Validation("comment", "A comment has at most 2000 characters.");
        }

        return await runs.DecideAsync(caller.TenantId, approvalId, caller.UserId, request.Outcome, request.Comment, cancellationToken) switch
        {
            RunService.DecisionResult.NotFound => ApiErrors.NotFound(),
            RunService.DecisionResult.AlreadyDecided => ApiErrors.Conflict("alreadyDecided", "The approval has been decided or cancelled."),
            _ => TypedResults.Ok(ToDto((await WaitQueries.ApprovalAsync(db, caller.TenantId, approvalId, cancellationToken))!)),
        };
    }

    private static ApprovalDto ToDto(ApprovalRequest a) => new(
        a.Id, a.RunId, a.Node, a.WorkspaceId, a.ListId, a.ItemId, a.Title, a.Status, a.Escalated, a.DueAt, a.DecidedBy, a.DecidedAt, a.Comment, a.CreatedAt);
}
