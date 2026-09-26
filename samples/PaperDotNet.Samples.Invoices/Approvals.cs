using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Extensions;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Samples.Invoices;

/// <summary>Who approved which invoice (the extension's own table, EXT-07).</summary>
public sealed class ApprovalRecord : ITenantOwned, IAuditable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid ListId { get; set; }

    public Guid ItemId { get; set; }

    public decimal Amount { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

/// <summary>The sample's tables, in the schema <c>ext_samples_invoices</c>.</summary>
public sealed class InvoicesDbContext(DbContextOptions<InvoicesDbContext> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
{
    public DbSet<ApprovalRecord> Approvals => Set<ApprovalRecord>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ApprovalRecord>(b =>
        {
            b.ToTable("approvals");
            b.Property(a => a.Amount).HasPrecision(18, 2);
            b.Property(a => a.Comment).HasMaxLength(500);
            b.HasIndex(a => a.ItemId);
        });
}

public sealed record ApproveRequest(string? Comment);

public sealed record InvoiceApprovalResponse(Guid Id, Guid WorkspaceId, Guid ListId, Guid ItemId, decimal Amount, string? Comment, Guid? ApprovedBy, DateTimeOffset ApprovedAt)
{
    internal static InvoiceApprovalResponse From(ApprovalRecord r) => new(r.Id, r.WorkspaceId, r.ListId, r.ItemId, r.Amount, r.Comment, r.CreatedBy, r.CreatedAt);
}

/// <summary>Approves invoices through <see cref="IListItemStore"/> (as the caller) and records who approved them.</summary>
internal static class ApprovalEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}/approve", ApproveAsync)
            .RequireScope($"{InvoicesExtension.Id}.approve");
        api.MapGet("/approvals", ListAsync).RequireScope($"{InvoicesExtension.Id}.read");
    }

    private static async Task<Results<Ok<InvoiceApprovalResponse>, ProblemHttpResult, ValidationProblem>> ApproveAsync(
        Guid workspaceId, Guid listId, Guid itemId, ApproveRequest? request, IListItemStore items, InvoicesDbContext db, CancellationToken ct)
    {
        var item = await items.GetAsync(workspaceId, listId, itemId, ct);
        if (item is null)
        {
            return ApiErrors.NotFound();
        }

        if (item.Fields["status"]?.GetValue<string>() != "pendingApproval")
        {
            return ApiErrors.Conflict("notPendingApproval", "The invoice does not wait for approval.");
        }

        // The item and the approval record live in different stores: the record is written after the item changed.
        var result = await items.UpdateAsync(workspaceId, listId, itemId, new JsonObject { ["status"] = "approved" }, item.Version, ct);
        switch (result.Status)
        {
            case ListItemStatus.Ok:
                break;
            case ListItemStatus.NotFound:
                return ApiErrors.NotFound();
            case ListItemStatus.Forbidden:
                return ApiErrors.Problem(StatusCodes.Status403Forbidden, "accessDenied", "You may not change this invoice.");
            case ListItemStatus.VersionMismatch:
                return ApiErrors.PreconditionFailed();
            case ListItemStatus.Invalid:
                return ApiErrors.Validation(result.Errors!.ToDictionary());
            default:
                return ApiErrors.Conflict("rejected", result.Message ?? "The change was rejected.");
        }

        var record = new ApprovalRecord
        {
            Id = Ids.New(),
            WorkspaceId = workspaceId,
            ListId = listId,
            ItemId = itemId,
            Amount = item.Fields["amount"]?.GetValue<decimal>() ?? 0,
            Comment = request?.Comment,
        };
        db.Approvals.Add(record);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(InvoiceApprovalResponse.From(record));
    }

    private static async Task<Ok<List<InvoiceApprovalResponse>>> ListAsync(InvoicesDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.Approvals.AsNoTracking().OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync(ct))
            .Select(InvoiceApprovalResponse.From)
            .ToList());
}
