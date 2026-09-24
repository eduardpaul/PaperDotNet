using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record MeResponse(
    Guid Id,
    string? UserName,
    string? DisplayName,
    string? Email,
    Guid TenantId,
    string? TenantIdentifier,
    IReadOnlyList<string> Scopes);

public sealed record ApiTokenResponse(
    Guid Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt);

public sealed record CreateApiTokenRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: Required, MinLength(1)] IReadOnlyList<string> Scopes,
    [property: Range(1, 3650)] int? ExpiresInDays);

/// <summary>Returned once, at creation: the only time the secret is visible.</summary>
public sealed record CreatedApiTokenResponse(ApiTokenResponse Token, string Secret);

internal static class MeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var me = endpoints.MapV1Group("me", "Me");
        me.MapGet("", GetMeAsync).WithName("GetMe");
        me.MapGet("/apiTokens", ListTokensAsync).WithName("ListMyApiTokens");
        me.MapPost("/apiTokens", CreateTokenAsync).WithName("CreateMyApiToken");
        me.MapDelete("/apiTokens/{id:guid}", RevokeTokenAsync).WithName("RevokeMyApiToken");
    }

    private static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> GetMeAsync(
        ICurrentUser current, ITenantContext tenant, IdentityDbContext db, IEffectiveScopeProvider scopes, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == current.UserId, ct);
        if (user is null)
        {
            return ApiErrors.NotFound();
        }

        var granted = await scopes.GetScopesAsync(user.Id, ct) ?? new HashSet<string>();
        return TypedResults.Ok(new MeResponse(
            user.Id, user.UserName, user.DisplayName, user.Email, user.TenantId, tenant.TenantIdentifier, granted.Order().ToList()));
    }

    private static async Task<Ok<List<ApiTokenResponse>>> ListTokensAsync(
        ICurrentUser current, ITenantContext tenant, IdentityDbContext db, CancellationToken ct)
    {
        var tokens = await db.ApiTokens.AsNoTracking()
            .Where(t => t.TenantId == tenant.TenantId && t.UserId == current.UserId && t.RevokedAt == null)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);
        return TypedResults.Ok(tokens.Select(ToResponse).ToList());
    }

    private static async Task<Results<Created<CreatedApiTokenResponse>, ValidationProblem>> CreateTokenAsync(
        CreateApiTokenRequest request,
        ICurrentUser current,
        ITenantContext tenant,
        IdentityDbContext db,
        IEffectiveScopeProvider scopes,
        TimeProvider time,
        CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var granted = await scopes.GetScopesAsync(current.UserId!.Value, ct) ?? new HashSet<string>();
        var notGranted = request.Scopes.Where(s => !granted.Contains(s)).ToList();
        if (notGranted.Count > 0)
        {
            return ApiErrors.Validation(new Dictionary<string, string[]>
            {
                ["scopes"] = [$"You can only grant scopes you hold. Not held: {string.Join(", ", notGranted)}."],
            });
        }

        var (secret, prefix, hash) = ApiTokenSecret.Create();
        var now = time.GetUtcNow();
        var token = new ApiToken
        {
            Id = Ids.New(),
            TenantId = tenant.TenantId!.Value,
            TenantIdentifier = tenant.TenantIdentifier!,
            UserId = current.UserId.Value,
            Name = request.Name.Trim(),
            Prefix = prefix,
            Hash = hash,
            Scopes = request.Scopes.Distinct().ToList(),
            CreatedAt = now,
            ExpiresAt = request.ExpiresInDays is { } days ? now.AddDays(days) : null,
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"{ApiRoutes.V1}/me/apiTokens/{token.Id}", new CreatedApiTokenResponse(ToResponse(token), secret));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeTokenAsync(
        Guid id, ICurrentUser current, ITenantContext tenant, IdentityDbContext db, TimeProvider time, CancellationToken ct)
    {
        var revoked = await db.ApiTokens
            .Where(t => t.Id == id && t.TenantId == tenant.TenantId && t.UserId == current.UserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, time.GetUtcNow()), ct);
        return revoked == 0 ? ApiErrors.NotFound() : TypedResults.NoContent();
    }

    private static ApiTokenResponse ToResponse(ApiToken t) =>
        new(t.Id, t.Name, t.Prefix, t.Scopes, t.CreatedAt, t.ExpiresAt, t.LastUsedAt);
}
