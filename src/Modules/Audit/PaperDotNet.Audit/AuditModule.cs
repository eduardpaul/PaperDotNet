using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Persistence;

namespace PaperDotNet.Audit;

public sealed class AuditModule : IModule
{
    public const string ReadScope = "audit.read";

    public string Name => "Audit";

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScopes(new ScopeDefinition(ReadScope, "Read the organization's audit log."));

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapV1Group("auditLog", "Audit log")
            .MapGet("", AuditLogEndpoint.ListAsync)
            .RequireScope(ReadScope)
            .WithName("ListAuditLog");
}

public sealed record AuditEntryResponse(
    Guid Id, DateTimeOffset At, Guid? UserId, AuditAction Action, string EntityType, Guid? EntityId, IReadOnlyList<string> Properties, string? TraceId);

public sealed record AuditPage(
    [property: JsonPropertyName("value")] IReadOnlyList<AuditEntryResponse> Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

/// <summary>
/// Newest first, across the audit tables of every module. Each module is queried
/// with the same keyset (<c>at</c>, <c>id</c>) and the results are merged by time.
/// </summary>
internal static class AuditLogEndpoint
{
    public static async Task<Results<Ok<AuditPage>, ValidationProblem>> ListAsync(
        string? entityType, Guid? entityId, Guid? userId, DateTimeOffset? from, DateTimeOffset? to,
        HttpRequest http, IServiceProvider services, ModuleDbContextRegistry registry, CancellationToken ct)
    {
        var page = PageRequest.From(http);
        Cursor? cursor = null;
        if (http.Query.TryGetValue("$skiptoken", out var token) && (cursor = Cursor.Decode(token.ToString())) is null)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]> { ["$skiptoken"] = ["Invalid token."] });
        }

        var sources = new List<List<AuditEntry>>();
        foreach (var type in registry.Contexts)
        {
            var db = (DbContext)services.GetRequiredService(type);
            var query = db.Set<AuditEntry>().AsNoTracking();
            if (entityType is not null)
            {
                query = query.Where(a => a.EntityType == entityType);
            }

            if (entityId is not null)
            {
                query = query.Where(a => a.EntityId == entityId);
            }

            if (userId is not null)
            {
                query = query.Where(a => a.UserId == userId);
            }

            if (from is { } start)
            {
                query = query.Where(a => a.At >= start);
            }

            if (to is { } end)
            {
                query = query.Where(a => a.At < end);
            }

            if (cursor is { } c)
            {
                query = query.Where(a => a.At < c.At || (a.At == c.At && a.Id.CompareTo(c.Id) < 0));
            }

            sources.Add(await query.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(page.Top + 1).ToListAsync(ct));
        }

        // Merge by time; entries of one module keep their database order (consistent with the keyset).
        var merged = new List<AuditEntry>();
        var positions = new int[sources.Count];
        while (merged.Count <= page.Top)
        {
            var best = -1;
            for (var i = 0; i < sources.Count; i++)
            {
                if (positions[i] < sources[i].Count && (best < 0 || sources[i][positions[i]].At > sources[best][positions[best]].At))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            merged.Add(sources[best][positions[best]++]);
        }

        string? nextLink = null;
        if (merged.Count > page.Top)
        {
            merged.RemoveAt(merged.Count - 1);
            var last = merged[^1];
            var query = http.Query
                .Where(q => q.Key is not "$skiptoken")
                .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}")
                .Append($"$skiptoken={new Cursor(last.At, last.Id).Encode()}");
            nextLink = $"{http.Scheme}://{http.Host}{http.PathBase}{http.Path}?{string.Join('&', query)}";
        }

        var value = merged
            .Select(a => new AuditEntryResponse(a.Id, a.At, a.UserId, a.Action, a.EntityType, a.EntityId, a.Properties, a.TraceId))
            .ToList();
        return TypedResults.Ok(new AuditPage(value, nextLink));
    }

    private readonly record struct Cursor(DateTimeOffset At, Guid Id)
    {
        public string Encode() =>
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{At.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{Id:N}"));

        public static Cursor? Decode(string token)
        {
            try
            {
                var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token)).Split(':');
                return parts.Length == 2
                       && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
                       && Guid.TryParseExact(parts[1], "N", out var id)
                    ? new Cursor(new DateTimeOffset(ticks, TimeSpan.Zero), id)
                    : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
