using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Core.Api;
using PaperDotNet.Core.Host.Data;
using PaperDotNet.Core.Security;

namespace PaperDotNet.Core.Host.Audit;

public sealed record AuditEntryDto(Guid Id, string Action, Guid TargetId, Guid? ListId, Guid? UserId, string? Summary, DateTimeOffset OccurredAt);

internal static class AuditEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/v1.0/audit", ListAsync).RequireScope(Scopes.AuditRead).WithTags("Audit").WithName("ListAuditEntries")
            .WithDescription("Newest first. Filter by target with ?targetId=.");

    private static AuditEntryDto ToDto(AuditEntry e) => new(e.Id, e.Action, e.TargetId, e.ListId, e.UserId, e.Summary, e.OccurredAt);

    private static async Task<Ok<Page<AuditEntryDto>>> ListAsync(HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, Guid? targetId, ClaimsPrincipalAccessor caller, CoreDb database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);
        var db = database;
        var tenant = caller.TenantId;
        var take = page.Top + 1;
        var ct = cancellationToken;
        List<AuditEntry> entries;

        // Static queries only (precompiled): one per combination of options.
        if (targetId is { } target)
        {
            entries = page.After is { } before
                ? await db.AuditEntries.Where(e => e.TenantId == tenant && e.TargetId == target && e.Id.CompareTo(before) < 0).OrderByDescending(e => e.Id).Take(take).ToListAsync(ct)
                : await db.AuditEntries.Where(e => e.TenantId == tenant && e.TargetId == target).OrderByDescending(e => e.Id).Take(take).ToListAsync(ct);
        }
        else
        {
            entries = page.After is { } before
                ? await db.AuditEntries.Where(e => e.TenantId == tenant && e.Id.CompareTo(before) < 0).OrderByDescending(e => e.Id).Take(take).ToListAsync(ct)
                : await db.AuditEntries.Where(e => e.TenantId == tenant).OrderByDescending(e => e.Id).Take(take).ToListAsync(ct);
        }

        return TypedResults.Ok(Page.Create([.. entries.Select(ToDto)], page, request, e => e.Id));
    }
}
