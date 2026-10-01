using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Audit.Data;

namespace PaperDotNet.Audit.Features;

/// <summary>A change: the entity type is <c>{module}.{Entity}</c> (e.g. <c>lists.ListItem</c>); properties are those an update changed.</summary>
public sealed record AuditLogEntryResponse(
    Guid Id, DateTimeOffset At, Guid? UserId, string Action, string EntityType, Guid? EntityId, IReadOnlyList<string> Properties, string? TraceId);

/// <summary>The audit log of every module (LST-14): who changed which entity, when and which properties.</summary>
internal static class AuditLogEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/v1.0/auditLog", ListAsync).RequireScope(AuditScopes.Read).WithTags("Audit").WithName("ListAuditLog")
            .WithDescription("Changes of every module, newest first. Filter by entityType (e.g. lists.ListItem), entityId, userId and time (from inclusive, to exclusive).");

    private static async Task<Ok<Page<AuditLogEntryResponse>>> ListAsync(
        HttpRequest request, [FromQuery(Name = "$top")] int? top, [FromQuery(Name = "$skiptoken")] string? skipToken, string? entityType, Guid? entityId,
        Guid? userId, DateTimeOffset? from, DateTimeOffset? to, Caller caller, AuditDbContext database, CancellationToken cancellationToken)
    {
        var page = PageRequest.Create(top, skipToken);

        // One precompiled query: absent filters are null parameters (ADR-0039). Ids are time-ordered (UUIDv7).
        var db = database;
        var tenant = caller.TenantId;
        var type = entityType;
        var entity = entityId;
        var user = userId;
        var fromMs = from?.ToUnixTimeMilliseconds();
        var toMs = to?.ToUnixTimeMilliseconds();
        var hasAfter = page.After is not null;
        var after = page.After ?? Guid.Empty;
        var take = page.Top + 1;
        var ct = cancellationToken;
        var entries = await db.AuditLog.AsNoTracking()
            .Where(a => a.TenantId == tenant
                && (type == null || a.EntityType == type)
                && (entity == null || a.EntityId == entity)
                && (user == null || a.UserId == user)
                && (fromMs == null || a.AtUnixMs >= fromMs)
                && (toMs == null || a.AtUnixMs < toMs)
                && (!hasAfter || a.Id.CompareTo(after) < 0))
            .OrderByDescending(a => a.Id)
            .Take(take)
            .ToListAsync(ct);
        return TypedResults.Ok(Page.Create([.. entries.Select(ToResponse)], page, request, e => e.Id));
    }

    private static AuditLogEntryResponse ToResponse(AuditLogEntry e) => new(
        e.Id, DateTimeOffset.FromUnixTimeMilliseconds(e.AtUnixMs), e.UserId, e.Action, e.EntityType, e.EntityId,
        e.Properties is { Length: > 0 } properties ? properties.Split(',') : [], e.TraceId);
}
