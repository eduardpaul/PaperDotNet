using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record TokenRequest(
    [property: Required, StringLength(256)] string UserName,
    [property: Required, StringLength(256)] string Password);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // Stage 1 (ADR-0002): password → access token for local accounts. OAuth flows come with OpenIddict in P1.
        endpoints.MapGroup($"{ApiRoutes.V1}/auth")
            .WithTags("Authentication")
            .AllowAnonymous()
            .MapPost("/token", IssueTokenAsync)
            .WithName("IssueToken");
    }

    private static async Task<Results<Ok<TokenResponse>, ValidationProblem, ProblemHttpResult>> IssueTokenAsync(
        TokenRequest request,
        UserManager<User> users,
        AccessTokenIssuer issuer,
        ITenantContext tenant,
        TimeProvider time)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var user = await users.FindByNameAsync(request.UserName);
        if (user is null || user.IsDisabled || await users.IsLockedOutAsync(user))
        {
            return InvalidCredentials();
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return InvalidCredentials();
        }

        await users.ResetAccessFailedCountAsync(user);
        var token = issuer.Issue(user.Id, user.DisplayName ?? user.UserName, tenant.TenantId!.Value, tenant.TenantIdentifier!);
        var expiresIn = (int)(token.ExpiresAt - time.GetUtcNow()).TotalSeconds;
        return TypedResults.Ok(new TokenResponse(token.Token, "Bearer", expiresIn));
    }

    private static ProblemHttpResult InvalidCredentials() =>
        ApiErrors.Problem(StatusCodes.Status401Unauthorized, "invalidCredentials", "The user name or password is incorrect.");
}
