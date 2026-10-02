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

        var entries = await QueryAsync(database, caller.TenantId, entityType, entityId, userId,
            from?.ToUnixTimeMilliseconds() ?? long.MinValue, to?.ToUnixTimeMilliseconds() ?? long.MaxValue, page.After ?? MaxId, page.Top + 1, cancellationToken);
        return TypedResults.Ok(Page.Create([.. entries.Select(ToResponse)], page, request, e => e.Id));
    }

    /// <summary>Above every UUID: the first page starts below it.</summary>
    private static readonly Guid MaxId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <summary>
    /// Newest first below <paramref name="before"/>. One static query per combination of the equality filters: optional
    /// filters written as <c>(p == null || column == p)</c> do not precompile (ADR-0039); time bounds and the cursor are
    /// always applied, with open values when absent.
    /// </summary>
    private static Task<List<AuditLogEntry>> QueryAsync(
        AuditDbContext database, Guid tenantId, string? entityType, Guid? entityId, Guid? userId, long fromMs, long toMs, Guid before, int take,
        CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var type = entityType ?? "";
        var entity = entityId ?? Guid.Empty;
        var user = userId ?? Guid.Empty;
        var start = fromMs;
        var end = toMs;
        var after = before;
        var count = take;
        var ct = cancellationToken;
        return (entityType is not null, entityId is not null, userId is not null) switch
        {
            (false, false, false) => db.AuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.AtUnixMs >= start && a.AtUnixMs < end && a.Id.CompareTo(after) < 0)
                .OrderByDescending(a => a.Id).Take(count).ToListAsync(ct),
            (true, false, false) => db.AuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.EntityType == type && a.AtUnixMs >= start && a.AtUnixMs < end && a.Id.CompareTo(after) < 0)
                .OrderByDescending(a => a.Id).Take(count).ToListAsync(ct),
            (false, true, false) => db.AuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.EntityId == entity && a.AtUnixMs >= start && a.AtUnixMs < end && a.Id.CompareTo(after) < 0)
                .OrderByDescending(a => a.Id).Take(count).ToListAsync(ct),
            (false, false, true) => db.AuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.UserId == user && a.AtUnixMs >= start && a.AtUnixMs < end && a.Id.CompareTo(after) < 0)
                .OrderByDescending(a => a.Id).Take(count).ToListAsync(ct),
            (true, true, false) => db.AuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.EntityType == type && a.EntityId == entity && a.AtUnixMs >= start && a.AtUnixMs < end && a.Id.CompareTo(after) < 0)
                .OrderByDescending(a => a.Id).Take(count).ToListAsync(ct),
            (true, false, true) => db.AuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.EntityType == type && a.UserId == user && a.AtUnixMs >= start && a.AtUnixMs < end && a.Id.CompareTo(after) < 0)
                .OrderByDescending(a => a.Id).Take(count).ToListAsync(ct),
            (false, true, true) => db.AuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.EntityId == entity && a.UserId == user && a.AtUnixMs >= start && a.AtUnixMs < end && a.Id.CompareTo(after) < 0)
                .OrderByDescending(a => a.Id).Take(count).ToListAsync(ct),
            (true, true, true) => db.AuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.EntityType == type && a.EntityId == entity && a.UserId == user && a.AtUnixMs >= start && a.AtUnixMs < end
                    && a.Id.CompareTo(after) < 0)
                .OrderByDescending(a => a.Id).Take(count).ToListAsync(ct),
        };
    }

    private static AuditLogEntryResponse ToResponse(AuditLogEntry e) => new(
        e.Id, DateTimeOffset.FromUnixTimeMilliseconds(e.AtUnixMs), e.UserId, e.Action, e.EntityType, e.EntityId,
        e.Properties is { Length: > 0 } properties ? properties.Split(',') : [], e.TraceId);
}
