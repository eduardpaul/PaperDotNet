using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Api;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Jobs.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Calendar.Features;

public sealed record CalendarSourceRequest(string Name, string Url);
public sealed record CalendarSourceUpdate(string? Name, string? Url, bool? Paused, bool Reset = false);
public sealed record CalendarSourceResponse(Guid Id, string Name, bool Paused, DateTimeOffset? LastSuccess,
    string? Error, int Created, int Updated, int Removed, bool Refreshing, [property: JsonPropertyName("@odata.etag")] string ETag);
public sealed record CalendarSourceItemResponse(Guid? SourceId, string? Name, IReadOnlyList<string> ManagedFields);
public sealed record CalendarRefreshResponse(Guid OperationId);
internal sealed record CalendarRefreshPayload(Guid WorkspaceId, Guid ListId, Guid SourceId);

internal static class CalendarSourceEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var list = endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/calendarSources", "Calendar");
        list.MapGet("/", ListAsync).RequireScope(CalendarScopes.Read).WithName("ListCalendarSources");
        list.MapPost("/", CreateAsync).RequireScope(CalendarScopes.Write).WithName("CreateCalendarSource");
        list.MapPut("/{sourceId:guid}", UpdateAsync).RequireScope(CalendarScopes.Write).WithName("UpdateCalendarSource");
        list.MapDelete("/{sourceId:guid}", RemoveAsync).RequireScope(CalendarScopes.Write).WithName("RemoveCalendarSource");
        list.MapPost("/{sourceId:guid}/refresh", RefreshAsync).RequireScope(CalendarScopes.Write).WithName("RefreshCalendarSource");
        endpoints.MapV1Group("workspaces/{workspaceId:guid}/lists/{listId:guid}/items/{itemId:guid}", "Calendar")
            .MapGet("/calendarSource", ItemSourceAsync).RequireScope(CalendarScopes.Read).WithName("GetItemCalendarSource");
    }

    private static async Task<ProblemHttpResult?> AccessAsync(Guid workspaceId, Guid listId, IListItemStore items, bool manage, CancellationToken ct)
    {
        var list = await items.GetListAsync(workspaceId, listId, ct);
        if (list is null || !list.ContentTypeKeys.Contains(CalendarService.EventKey))
        {
            return ApiErrors.NotFound("The list does not support calendar events.");
        }

        return manage && list.Access < WorkspaceAccessLevel.Manage ? CalendarAccess.Forbidden() : null;
    }

    private static CalendarSourceResponse Response(CalendarSubscription s, TimeProvider time) =>
        new(s.Id, s.Name, s.Paused, s.LastSuccess, s.Error, s.Created, s.Updated, s.Removed,
            s.LeaseUntil > time.GetUtcNow(), ETags.From(s.Version));

    private static async Task<Results<Ok<List<CalendarSourceResponse>>, ProblemHttpResult>> ListAsync(
        Guid workspaceId, Guid listId, IListItemStore items, CalendarDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (await AccessAsync(workspaceId, listId, items, false, ct) is { } error) { return error; }
        var sources = await db.Subscriptions.AsNoTracking().Where(s => s.WorkspaceId == workspaceId && s.ListId == listId).OrderBy(s => s.Name).ToListAsync(ct);
        return TypedResults.Ok(sources.Select(s => Response(s, time)).ToList());
    }

    private static async Task<ProblemHttpResult?> ValidateAsync(string? name, string? url, CalendarSourceHttp http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 || !CalendarSourceHttp.IsValidUrl(url))
        {
            return ApiErrors.Problem(400, "invalidCalendarSource", "Provide a name (up to 200 characters) and a public HTTPS iCalendar URL.");
        }

        try
        {
            var download = await http.DownloadAsync(url!, null, null, ct);
            CalendarSourceReplication.ParseSnapshot(download.Text ?? string.Empty);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            return ApiErrors.Problem(400, "invalidCalendarSource", ex is CalendarSourceException ? ex.Message
                : "The source could not be fetched or parsed. Check the iCalendar URL.");
        }
    }

    private static string Hash(string url) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    private static async Task<Results<Created<CalendarSourceResponse>, ProblemHttpResult>> CreateAsync(
        Guid workspaceId, Guid listId, CalendarSourceRequest request, IListItemStore items, CalendarDbContext db,
        CalendarSourceHttp http, CalendarSourceReplication replication, IWorkflowDirectory workflows, IOperations operations,
        TimeProvider time, CancellationToken ct)
    {
        if (await AccessAsync(workspaceId, listId, items, true, ct) is { } denied) { return denied; }
        if (await ValidateAsync(request.Name, request.Url, http, ct) is { } invalid) { return invalid; }
        var hash = Hash(request.Url);
        if (await db.Subscriptions.AnyAsync(s => s.ListId == listId && s.UrlHash == hash, ct))
        {
            return ApiErrors.Conflict("calendarSourceExists", "This list already subscribes to that URL.");
        }

        if (!await workflows.EnableBuiltInAsync(workspaceId, CalendarSourceWorkflow.Key, ct))
        {
            return ApiErrors.Conflict("calendarWorkflowUnavailable", "The calendar refresh workflow could not be enabled. Check workspace workflows.");
        }

        var source = new CalendarSubscription
        {
            Id = PaperDotNet.Abstractions.Ids.New(),
            WorkspaceId = workspaceId,
            ListId = listId,
            Name = request.Name.Trim(),
            ProtectedUrl = replication.Protector.Protect(request.Url),
            UrlHash = hash
        };
        db.Subscriptions.Add(source);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return ApiErrors.Conflict("calendarSourceExists", "This list already subscribes to that URL."); }
        await operations.StartAsync(CalendarSourceOperation.OperationType, new CalendarRefreshPayload(workspaceId, listId, source.Id), ct);
        return TypedResults.Created($"{ApiRoutes.V1}/workspaces/{workspaceId}/lists/{listId}/calendarSources/{source.Id}", Response(source, time));
    }

    private static async Task<(CalendarSubscription? Source, ProblemHttpResult? Error)> ForChangeAsync(
        Guid workspaceId, Guid listId, Guid sourceId, HttpRequest request, IListItemStore items, CalendarDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (await AccessAsync(workspaceId, listId, items, true, ct) is { } denied) { return (null, denied); }
        var source = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == sourceId && s.WorkspaceId == workspaceId && s.ListId == listId, ct);
        if (source is null) { return (null, ApiErrors.NotFound()); }
        if (!ETags.TryGetIfMatch(request, out var version)) { return (null, ApiErrors.PreconditionRequired()); }
        if (source.Version != version) { return (null, ApiErrors.PreconditionFailed()); }
        if (source.LeaseUntil > time.GetUtcNow()) { return (null, ApiErrors.Conflict("sourceRefreshing", "A refresh is running. Try again when it finishes.")); }
        return (source, null);
    }

    private static async Task<Guid?> LockConfigurationAsync(CalendarSubscription source, CalendarDbContext db, TimeProvider time, CancellationToken ct)
    {
        var id = PaperDotNet.Abstractions.Ids.New();
        var now = time.GetUtcNow();
        var changed = await db.Subscriptions.Where(s => s.Id == source.Id && s.Version == source.Version
            && (s.LeaseUntil == null || s.LeaseUntil <= now)).ExecuteUpdateAsync(s => s.SetProperty(p => p.LeaseId, id)
                .SetProperty(p => p.LeaseUntil, now.AddMinutes(5)), ct);
        return changed == 1 ? id : null;
    }

    private static async Task<Results<Ok<CalendarSourceResponse>, ProblemHttpResult>> UpdateAsync(
        Guid workspaceId, Guid listId, Guid sourceId, CalendarSourceUpdate request, HttpRequest httpRequest, HttpResponse response,
        IListItemStore items, CalendarDbContext db, CalendarSourceHttp http, CalendarSourceReplication replication, IOperations operations,
        TimeProvider time, CancellationToken ct)
    {
        var (source, error) = await ForChangeAsync(workspaceId, listId, sourceId, httpRequest, items, db, time, ct);
        if (error is not null) { return error; }
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            { return ApiErrors.Problem(400, "invalidCalendarSource", "Provide a name up to 200 characters."); }
            source!.Name = request.Name.Trim();
        }

        if (request.Url is not null)
        {
            if (await ValidateAsync(source!.Name, request.Url, http, ct) is { } invalid) { return invalid; }
            var hash = Hash(request.Url);
            if (await db.Subscriptions.AnyAsync(s => s.ListId == listId && s.Id != sourceId && s.UrlHash == hash, ct))
            { return ApiErrors.Conflict("calendarSourceExists", "This list already subscribes to that URL."); }
            source.ProtectedUrl = replication.Protector.Protect(request.Url);
            source.UrlHash = hash;
            source.HttpETag = null;
            source.HttpLastModified = null;
        }

        if (request.Paused is { } paused) { source!.Paused = paused; }
        if (request.Reset)
        {
            // Save config with the expected version before detaching the old feed's items.
            source!.HttpETag = null;
            source.HttpLastModified = null;
        }
        var lockId = await LockConfigurationAsync(source!, db, time, ct);
        if (lockId is null) { return ApiErrors.Conflict("sourceRefreshing", "This source is refreshing or was changed. Reload and try again."); }
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            if (request.Reset) { await db.Sources.Where(s => s.SubscriptionId == sourceId).ExecuteDeleteAsync(ct); }
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { return ApiErrors.PreconditionFailed(); }
        finally
        {
            await db.Subscriptions.Where(s => s.Id == sourceId && s.LeaseId == lockId).ExecuteUpdateAsync(s =>
                s.SetProperty(p => p.LeaseId, (Guid?)null).SetProperty(p => p.LeaseUntil, (DateTimeOffset?)null), CancellationToken.None);
        }
        if (!source!.Paused)
        { await operations.StartAsync(CalendarSourceOperation.OperationType, new CalendarRefreshPayload(workspaceId, listId, sourceId), ct); }
        ETags.Set(response, source.Version);
        return TypedResults.Ok(Response(source, time));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        Guid workspaceId, Guid listId, Guid sourceId, HttpRequest request, IListItemStore items, CalendarDbContext db, TimeProvider time, CancellationToken ct)
    {
        var (source, error) = await ForChangeAsync(workspaceId, listId, sourceId, request, items, db, time, ct);
        if (error is not null) { return error; }
        var lockId = await LockConfigurationAsync(source!, db, time, ct);
        if (lockId is null) { return ApiErrors.Conflict("sourceRefreshing", "This source is refreshing or was changed. Reload and try again."); }
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.Subscriptions.Remove(source!);
            await db.SaveChangesAsync(ct);
            await db.Sources.Where(s => s.SubscriptionId == sourceId).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { return ApiErrors.PreconditionFailed(); }
        finally
        {
            await db.Subscriptions.Where(s => s.Id == sourceId && s.LeaseId == lockId).ExecuteUpdateAsync(s =>
                s.SetProperty(p => p.LeaseId, (Guid?)null).SetProperty(p => p.LeaseUntil, (DateTimeOffset?)null), CancellationToken.None);
        }
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted<CalendarRefreshResponse>, ProblemHttpResult>> RefreshAsync(
        Guid workspaceId, Guid listId, Guid sourceId, IListItemStore items, CalendarDbContext db, IOperations operations, CancellationToken ct)
    {
        if (await AccessAsync(workspaceId, listId, items, true, ct) is { } denied) { return denied; }
        if (!await db.Subscriptions.AnyAsync(s => s.Id == sourceId && s.WorkspaceId == workspaceId && s.ListId == listId && !s.Paused, ct))
        { return ApiErrors.NotFound("An active calendar source was not found."); }
        var id = await operations.StartAsync(CalendarSourceOperation.OperationType, new CalendarRefreshPayload(workspaceId, listId, sourceId), ct);
        return TypedResults.Accepted($"{ApiRoutes.V1}/operations/{id}", new CalendarRefreshResponse(id));
    }

    private static async Task<Results<Ok<CalendarSourceItemResponse>, ProblemHttpResult>> ItemSourceAsync(
        Guid workspaceId, Guid listId, Guid itemId, IListItemStore items, CalendarDbContext db, CancellationToken ct)
    {
        if (await items.GetAsync(workspaceId, listId, itemId, ct) is null) { return ApiErrors.NotFound(); }
        var source = await (from link in db.Sources
                            from subscription in db.Subscriptions
                            where link.ItemId == itemId && link.ListId == listId && link.SubscriptionId == subscription.Id
                                && subscription.WorkspaceId == workspaceId
                            select subscription).AsNoTracking().FirstOrDefaultAsync(ct);
        return TypedResults.Ok(new CalendarSourceItemResponse(source?.Id, source?.Name, source is null ? [] : CalendarSourceMutator.Fields));
    }
}

internal sealed class CalendarSourceOperation(CalendarSourceReplication replication, CalendarDbContext db, IListItemStore items)
    : OperationHandler<CalendarRefreshPayload>
{
    public const string OperationType = "calendar.source.refresh";
    public override string Type => OperationType;
    protected override async Task<object?> ExecuteAsync(CalendarRefreshPayload payload, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var list = await items.GetListAsync(payload.WorkspaceId, payload.ListId, cancellationToken);
        if (list?.Access < WorkspaceAccessLevel.Manage || list is null)
        { throw new InvalidOperationException("You no longer manage the destination list."); }
        if (!await db.Subscriptions.AnyAsync(s => s.Id == payload.SourceId && s.ListId == payload.ListId && s.WorkspaceId == payload.WorkspaceId, cancellationToken))
        { throw new InvalidOperationException("The source no longer exists."); }
        return new { refreshed = await replication.RefreshAsync(payload.SourceId, cancellationToken) };
    }
}
