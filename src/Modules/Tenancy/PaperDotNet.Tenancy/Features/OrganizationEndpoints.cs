using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Tenancy.Data;

namespace PaperDotNet.Tenancy.Features;

public sealed record OrganizationResponse(Guid Id, string Identifier, string DisplayName);

internal static class OrganizationEndpoints
{
    /// <summary><c>GET /v1.0/organization</c>: the caller's tenant (Graph naming).</summary>
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapV1Group("organization", "Organization")
            .MapGet("", GetAsync)
            .WithName("GetOrganization");
    }

    private static async Task<Results<Ok<OrganizationResponse>, ProblemHttpResult>> GetAsync(
        ITenantContext tenant, TenancyDbContext db, CancellationToken ct)
    {
        var entity = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenant.TenantId, ct);
        return entity is null
            ? ApiErrors.NotFound()
            : TypedResults.Ok(new OrganizationResponse(entity.Id, entity.Identifier, entity.Name));
    }
}
