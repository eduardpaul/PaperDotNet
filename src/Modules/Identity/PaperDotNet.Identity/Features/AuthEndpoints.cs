using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Authentication;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record LoginRequest(
    [property: Required, StringLength(256)] string UserName,
    [property: Required, StringLength(256)] string Password);

public sealed record PasskeyLoginOptionsRequest(string? UserName);

/// <summary>WebAuthn options for <c>navigator.credentials.create/get</c> plus an opaque state to send back.</summary>
public sealed record PasskeyOptionsResponse(JsonElement Options, string State);

public sealed record PasskeyLoginRequest([property: Required] JsonElement Credential, [property: Required] string State);

public sealed record PasskeyRegistrationRequest([property: Required] JsonElement Credential, [property: Required] string State, [property: StringLength(100)] string? Name);

public sealed record PasskeyResponse(string Id, string? Name, DateTimeOffset CreatedAt, bool IsBackedUp);

/// <summary>
/// Interactive sign-in (IAM-01): a session cookie, used only to complete
/// <c>/connect/authorize</c>. APIs are called with OAuth access tokens or API tokens.
/// </summary>
internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup($"{ApiRoutes.V1}/auth").WithTags("Authentication").AllowAnonymous();
        auth.MapPost("/login", LoginAsync).WithName("Login");
        auth.MapPost("/logout", (Delegate)LogoutAsync).WithName("Logout");
        auth.MapPost("/passkeys/options", PasskeyLoginOptionsAsync).WithName("PasskeyLoginOptions");
        auth.MapPost("/passkeys/login", PasskeyLoginAsync).WithName("PasskeyLogin");

        var me = endpoints.MapV1Group("me/passkeys", "Me");
        me.MapGet("", ListPasskeysAsync).WithName("ListMyPasskeys");
        me.MapPost("/options", PasskeyCreationOptionsAsync).WithName("PasskeyCreationOptions");
        me.MapPost("", RegisterPasskeyAsync).WithName("RegisterPasskey");
        me.MapDelete("/{id}", RemovePasskeyAsync).WithName("RemovePasskey");
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> LoginAsync(
        LoginRequest request, HttpContext http, UserManager<User> users, ITenantContext tenant)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var user = await PasswordSignIn.CheckAsync(users, request.UserName, request.Password);
        if (user is null)
        {
            return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "invalidCredentials", "The user name or password is incorrect.");
        }

        await SignInSessionAsync(http, user, tenant, "pwd");
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(AuthSchemes.Session);
        return TypedResults.NoContent();
    }

    // ---- Passkey sign-in ----------------------------------------------------

    /// <summary>Request options; without a user name the authenticator offers its discoverable credentials.</summary>
    private static async Task<Ok<PasskeyOptionsResponse>> PasskeyLoginOptionsAsync(
        PasskeyLoginOptionsRequest? request, HttpContext http, UserManager<User> users, IPasskeyHandler<User> passkeys,
        PasskeyState state, ITenantContext tenant)
    {
        var user = request?.UserName is { Length: > 0 } name ? await users.FindByNameAsync(name) : null;
        var options = await passkeys.MakeRequestOptionsAsync(user, http);
        return TypedResults.Ok(new PasskeyOptionsResponse(Parse(options.RequestOptionsJson), state.Protect(tenant, null, options.AssertionState)));
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> PasskeyLoginAsync(
        PasskeyLoginRequest request, HttpContext http, UserManager<User> users, IPasskeyHandler<User> passkeys,
        PasskeyState state, ITenantContext tenant)
    {
        if (!state.TryUnprotect(request.State, tenant, null, out var assertionState))
        {
            return Invalid("state", "The sign-in attempt expired. Request new options.");
        }

        var result = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = http,
            CredentialJson = request.Credential.GetRawText(),
            AssertionState = assertionState,
        });
        if (!result.Succeeded || result.User is not { } user || !OAuthEndpoints.CanSignIn(user))
        {
            return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "invalidCredentials", "The passkey could not be verified.");
        }

        // Stores the new signature counter (clone detection).
        await users.AddOrUpdatePasskeyAsync(user, result.Passkey);
        await SignInSessionAsync(http, user, tenant, "hwk");
        return TypedResults.NoContent();
    }

    // ---- My passkeys --------------------------------------------------------

    private static async Task<Results<Ok<List<PasskeyResponse>>, ProblemHttpResult>> ListPasskeysAsync(UserManager<User> users, ICurrentUser current)
    {
        if (await FindCurrentAsync(users, current) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        var passkeys = await users.GetPasskeysAsync(user);
        return TypedResults.Ok(passkeys.Select(p => new PasskeyResponse(Base64Url.EncodeToString(p.CredentialId), p.Name, p.CreatedAt, p.IsBackedUp)).ToList());
    }

    private static async Task<Results<Ok<PasskeyOptionsResponse>, ProblemHttpResult>> PasskeyCreationOptionsAsync(
        HttpContext http, UserManager<User> users, ICurrentUser current, IPasskeyHandler<User> passkeys, PasskeyState state, ITenantContext tenant)
    {
        if (await FindCurrentAsync(users, current) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        var entity = new PasskeyUserEntity
        {
            Id = user.Id.ToString(),
            Name = user.UserName!,
            DisplayName = user.DisplayName ?? user.UserName!,
        };
        var options = await passkeys.MakeCreationOptionsAsync(entity, http);
        return TypedResults.Ok(new PasskeyOptionsResponse(Parse(options.CreationOptionsJson), state.Protect(tenant, user.Id, options.AttestationState)));
    }

    private static async Task<Results<Created<PasskeyResponse>, ValidationProblem, ProblemHttpResult>> RegisterPasskeyAsync(
        PasskeyRegistrationRequest request, HttpContext http, UserManager<User> users, ICurrentUser current, IPasskeyHandler<User> passkeys,
        PasskeyState state, ITenantContext tenant)
    {
        if (await FindCurrentAsync(users, current) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        if (!state.TryUnprotect(request.State, tenant, user.Id, out var attestationState))
        {
            return Invalid("state", "The registration expired. Request new options.");
        }

        var result = await passkeys.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = http,
            CredentialJson = request.Credential.GetRawText(),
            AttestationState = attestationState,
        });
        if (!result.Succeeded || result.UserEntity.Id != user.Id.ToString())
        {
            return Invalid("credential", result.Failure?.Message ?? "The passkey could not be verified.");
        }

        var passkey = result.Passkey;
        passkey.Name = request.Name?.Trim() is { Length: > 0 } name ? name : "Passkey";
        var saved = await users.AddOrUpdatePasskeyAsync(user, passkey);
        if (!saved.Succeeded)
        {
            return Invalid("credential", string.Join(" ", saved.Errors.Select(e => e.Description)));
        }

        var id = Base64Url.EncodeToString(passkey.CredentialId);
        return TypedResults.Created($"{ApiRoutes.V1}/me/passkeys/{id}", new PasskeyResponse(id, passkey.Name, passkey.CreatedAt, passkey.IsBackedUp));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemovePasskeyAsync(string id, UserManager<User> users, ICurrentUser current)
    {
        if (await FindCurrentAsync(users, current) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        byte[] credentialId;
        try
        {
            credentialId = Base64Url.DecodeFromChars(id);
        }
        catch (FormatException)
        {
            return ApiErrors.NotFound();
        }

        if (await users.GetPasskeyAsync(user, credentialId) is null)
        {
            return ApiErrors.NotFound();
        }

        await users.RemovePasskeyAsync(user, credentialId);
        return TypedResults.NoContent();
    }

    // ---- Helpers ------------------------------------------------------------

    /// <summary>The user's security stamp at sign-in: a password change or reset ends the session (IAM-14).</summary>
    internal const string SessionStampClaim = "stamp";

    internal static async Task SignInSessionAsync(HttpContext http, User user, ITenantContext tenant, string method)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(PaperDotNetClaims.UserId, user.Id.ToString()),
                new Claim(PaperDotNetClaims.Name, user.DisplayName ?? user.UserName ?? string.Empty),
                new Claim(PaperDotNetClaims.TenantId, tenant.TenantId!.Value.ToString()),
                new Claim(PaperDotNetClaims.TenantIdentifier, tenant.TenantIdentifier!),
                new Claim("amr", method),
                new Claim(SessionStampClaim, user.SecurityStamp ?? string.Empty),
            ],
            AuthSchemes.Session,
            PaperDotNetClaims.Name,
            "role");
        await http.SignInAsync(AuthSchemes.Session, new ClaimsPrincipal(identity));
    }

    private static async Task<User?> FindCurrentAsync(UserManager<User> users, ICurrentUser current) =>
        current.UserId is { } id ? await users.FindByIdAsync(id.ToString()) : null;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ValidationProblem Invalid(string key, string message) =>
        ApiErrors.Validation(new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>
/// Carries WebAuthn ceremony state through the client instead of server storage:
/// protected, bound to tenant (and user for registration), valid for five minutes.
/// </summary>
internal sealed class PasskeyState(IDataProtectionProvider dataProtection)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector("PaperDotNet.Identity.PasskeyState").ToTimeLimitedDataProtector();

    public string Protect(ITenantContext tenant, Guid? userId, string? state) =>
        _protector.Protect(JsonSerializer.Serialize(new Payload(tenant.TenantId!.Value, userId, state)), Lifetime);

    public bool TryUnprotect(string value, ITenantContext tenant, Guid? userId, out string? state)
    {
        state = null;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(value));
            if (payload is null || payload.TenantId != tenant.TenantId || payload.UserId != userId)
            {
                return false;
            }

            state = payload.State;
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }

    private sealed record Payload(Guid TenantId, Guid? UserId, string? State);
}
