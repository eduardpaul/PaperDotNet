using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Api;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;

    public Guid? UserId => Principal?.FindGuid(PaperDotNetClaims.UserId);

    public Guid? TenantId => Principal?.FindGuid(PaperDotNetClaims.TenantId);

    public bool IsAuthenticated => Principal is not null;

    public bool HasScope(string scope) => Principal?.HasScope(scope) ?? false;
}

/// <summary>The authenticated caller of an endpoint (tenant and user are always present behind authorization).</summary>
public sealed class Caller(ICurrentUser user)
{
    public Guid TenantId => user.TenantId ?? throw new InvalidOperationException("The caller has no tenant.");

    public Guid UserId => user.UserId ?? throw new InvalidOperationException("The caller has no user id.");

    /// <summary>The caller as the author of changes (causation depth 0: a person's change).</summary>
    public ChangeActor Actor => new(TenantId, UserId);
}

public static class ApiServiceCollectionExtensions
{
    /// <summary>The caller of the request (<see cref="ICurrentUser"/>, <see cref="Caller"/>).</summary>
    public static IServiceCollection AddPaperDotNetApi(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<Caller>();
        return services;
    }
}
