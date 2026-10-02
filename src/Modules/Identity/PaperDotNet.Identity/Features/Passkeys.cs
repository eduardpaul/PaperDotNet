using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Data;

namespace PaperDotNet.Identity.Features;

public sealed record PasskeyLoginOptionsRequest(string? UserName);

/// <summary>WebAuthn options for <c>navigator.credentials.create/get</c>, and an opaque state to send back with the answer.</summary>
public sealed record PasskeyOptionsResponse(JsonElement Options, string State);

public sealed record PasskeyLoginRequest(JsonElement Credential, string? State);

public sealed record PasskeyRegistrationRequest(JsonElement Credential, string? State, string? Name);

public sealed record PasskeyResponse(string Id, string Name, DateTimeOffset CreatedAt, bool IsBackedUp);

/// <summary>The ceremony state carried by the client: its tenant, the user (registration) and the handler's state.</summary>
public sealed record PasskeyStatePayload(Guid TenantId, Guid? UserId, string? State);

/// <summary>The tenant the passkey store looks users up in, set by the endpoints before the handler runs.</summary>
internal sealed class PasskeyTenant
{
    public Guid TenantId { get; set; }
}

/// <summary>
/// The user store ASP.NET Core Identity's <see cref="PasskeyHandler{TUser}"/> needs (through <see cref="UserManager{TUser}"/>):
/// users of one tenant and their passkeys. Only what passkeys use is supported; everything else about users stays in
/// <see cref="Users"/>.
/// </summary>
internal sealed class PasskeyUserStore(IdentityDbContext db, PasskeyTenant tenant, TimeProvider time) : IUserPasskeyStore<User>
{
    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.Id.ToString());

    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.UserName);

    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken) => throw new NotSupportedException("Users are created by the Identity module.");

    public async Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken) => throw new NotSupportedException("Users are deleted by the Identity module.");

    public Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(userId, out var id) ? Users.FindAsync(db, tenant.TenantId, id, cancellationToken) : Task.FromResult<User?>(null);

    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        var database = db;
        var tenantId = tenant.TenantId;
        var name = normalizedUserName;
        var ct = cancellationToken;
        return database.Users.Where(u => u.TenantId == tenantId && u.NormalizedUserName == name && u.DeletedAt == null).FirstOrDefaultAsync(ct);
    }

    public async Task AddOrUpdatePasskeyAsync(User user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
    {
        var stored = await FindAsync(user.TenantId, Base64Url.EncodeToString(passkey.CredentialId), cancellationToken);
        if (stored is null)
        {
            stored = new UserPasskey
            {
                Id = Ids.New(),
                TenantId = user.TenantId,
                UserId = user.Id,
                CredentialId = Base64Url.EncodeToString(passkey.CredentialId),
                CreatedAt = time.GetUtcNow(),
            };
            db.Passkeys.Add(stored);
        }
        else if (stored.UserId != user.Id)
        {
            throw new InvalidOperationException("The passkey belongs to another user.");
        }

        stored.Name = passkey.Name is { Length: > 0 } name ? name[..Math.Min(name.Length, 100)] : stored.Name;
        stored.PublicKey = passkey.PublicKey;
        stored.SignCount = passkey.SignCount;
        stored.Transports = string.Join(' ', passkey.Transports ?? []);
        stored.IsUserVerified = passkey.IsUserVerified;
        stored.IsBackupEligible = passkey.IsBackupEligible;
        stored.IsBackedUp = passkey.IsBackedUp;
        stored.AttestationObject = passkey.AttestationObject;
        stored.ClientDataJson = passkey.ClientDataJson;
    }

    public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(User user, CancellationToken cancellationToken)
    {
        var database = db;
        var tenantId = user.TenantId;
        var userId = user.Id;
        var ct = cancellationToken;
        var passkeys = await database.Passkeys.AsNoTracking().Where(p => p.TenantId == tenantId && p.UserId == userId).OrderBy(p => p.Id).ToListAsync(ct);
        return [.. passkeys.Select(ToInfo)];
    }

    public async Task<User?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken) =>
        await FindAsync(tenant.TenantId, Base64Url.EncodeToString(credentialId), cancellationToken) is { } passkey
            ? await Users.FindAsync(db, passkey.TenantId, passkey.UserId, cancellationToken)
            : null;

    public async Task<UserPasskeyInfo?> FindPasskeyAsync(User user, byte[] credentialId, CancellationToken cancellationToken) =>
        await FindAsync(user.TenantId, Base64Url.EncodeToString(credentialId), cancellationToken) is { } passkey && passkey.UserId == user.Id ? ToInfo(passkey) : null;

    public async Task RemovePasskeyAsync(User user, byte[] credentialId, CancellationToken cancellationToken)
    {
        if (await FindAsync(user.TenantId, Base64Url.EncodeToString(credentialId), cancellationToken) is { } passkey && passkey.UserId == user.Id)
        {
            db.Passkeys.Remove(passkey);
        }
    }

    public void Dispose()
    {
    }

    private Task<UserPasskey?> FindAsync(Guid tenantId, string credentialId, CancellationToken cancellationToken)
    {
        var database = db;
        var tenantKey = tenantId;
        var id = credentialId;
        var ct = cancellationToken;
        return database.Passkeys.Where(p => p.TenantId == tenantKey && p.CredentialId == id).FirstOrDefaultAsync(ct);
    }

    private static UserPasskeyInfo ToInfo(UserPasskey p) =>
        new(Base64Url.DecodeFromChars(p.CredentialId), p.PublicKey, p.CreatedAt, (uint)p.SignCount, OAuth.Split(p.Transports).ToArray(),
            p.IsUserVerified, p.IsBackupEligible, p.IsBackedUp, p.AttestationObject, p.ClientDataJson)
        {
            Name = p.Name,
        };
}

/// <summary>
/// Carries WebAuthn ceremony state through the client instead of server storage: protected, bound to the tenant (and
/// the user, for registration), valid for five minutes.
/// </summary>
internal sealed class PasskeyState(IDataProtectionProvider dataProtection)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector("PaperDotNet.Identity.PasskeyState").ToTimeLimitedDataProtector();

    public string Protect(Guid tenantId, Guid? userId, string? state) =>
        _protector.Protect(JsonSerializer.Serialize(new PasskeyStatePayload(tenantId, userId, state), IdentityJson.Default.PasskeyStatePayload), Lifetime);

    public bool TryUnprotect(string? value, Guid tenantId, Guid? userId, out string? state)
    {
        state = null;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize(_protector.Unprotect(value), IdentityJson.Default.PasskeyStatePayload);
            if (payload is null || payload.TenantId != tenantId || payload.UserId != userId)
            {
                return false;
            }

            state = payload.State;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Passkeys (IAM-01, WebAuthn through ASP.NET Core Identity's <see cref="PasskeyHandler{TUser}"/>): register them on
/// <c>/v1.0/me/passkeys</c> and sign in without a password at <c>/v1.0/auth/passkeys</c>, which starts a sign-in
/// session like <c>/v1.0/auth/login</c>.
/// </summary>
internal static class Passkeys
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/v1.0/auth/passkeys").WithTags("Auth").AllowAnonymous();
        auth.MapPost("/options", LoginOptionsAsync).RequireRateLimiting(RateLimits.SignIn).WithName("PasskeyLoginOptions")
            .WithDescription("Request options for navigator.credentials.get(); without a user name the authenticator offers its discoverable passkeys.");
        auth.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimits.SignIn).WithName("PasskeyLogin");

        var me = app.MapGroup("/v1.0/me/passkeys").WithTags("Me").RequireAuthorization();
        me.MapGet("", ListAsync).WithName("ListMyPasskeys");
        me.MapPost("/options", CreationOptionsAsync).WithName("PasskeyCreationOptions");
        me.MapPost("", RegisterAsync).WithName("RegisterPasskey");
        me.MapDelete("/{id}", RemoveAsync).WithName("RemovePasskey");
    }

    private static async Task<Results<Ok<PasskeyOptionsResponse>, ProblemHttpResult>> LoginOptionsAsync(
        PasskeyLoginOptionsRequest? request, HttpContext http, TenantResolver tenants, PasskeyTenant scope, UserManager<User> users,
        IPasskeyHandler<User> passkeys, PasskeyState state, CancellationToken cancellationToken)
    {
        if ((await tenants.ResolveOrDefaultAsync(http.Request, cancellationToken)).Id is not { } tenantId)
        {
            return ApiErrors.Problem(StatusCodes.Status404NotFound, "tenantNotFound", "No active tenant matches this request.");
        }

        scope.TenantId = tenantId;
        var user = request?.UserName is { Length: > 0 } name ? await users.FindByNameAsync(name) : null;
        var options = await passkeys.MakeRequestOptionsAsync(user, http);
        return TypedResults.Ok(new PasskeyOptionsResponse(JsonElement.Parse(options.RequestOptionsJson), state.Protect(tenantId, null, options.AssertionState)));
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> LoginAsync(
        PasskeyLoginRequest request, HttpContext http, TenantResolver tenants, PasskeyTenant scope, UserManager<User> users,
        IPasskeyHandler<User> passkeys, PasskeyState state, CancellationToken cancellationToken)
    {
        if ((await tenants.ResolveOrDefaultAsync(http.Request, cancellationToken)).Id is not { } tenantId
            || !state.TryUnprotect(request.State, tenantId, null, out var assertionState))
        {
            return ApiErrors.Validation("state", "The sign-in attempt expired. Request new options.");
        }

        scope.TenantId = tenantId;
        var result = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = http,
            CredentialJson = request.Credential.GetRawText(),
            AssertionState = assertionState,
        });
        if (!result.Succeeded || result.User is not { } user || !SignInSession.CanSignIn(user))
        {
            return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "invalidCredentials", "The passkey could not be verified.");
        }

        // Stores the new signature counter (clone detection).
        await users.AddOrUpdatePasskeyAsync(user, result.Passkey);
        await SignInSession.SignInAsync(http, user, "hwk");
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<List<PasskeyResponse>>, ProblemHttpResult>> ListAsync(
        Caller caller, PasskeyTenant scope, UserManager<User> users, IdentityDbContext db, CancellationToken cancellationToken)
    {
        if (await CurrentAsync(caller, scope, db, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        var passkeys = await users.GetPasskeysAsync(user);
        return TypedResults.Ok(passkeys.Select(ToResponse).ToList());
    }

    private static async Task<Results<Ok<PasskeyOptionsResponse>, ProblemHttpResult>> CreationOptionsAsync(
        HttpContext http, Caller caller, PasskeyTenant scope, IdentityDbContext db, IPasskeyHandler<User> passkeys, PasskeyState state, CancellationToken cancellationToken)
    {
        if (await CurrentAsync(caller, scope, db, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        var options = await passkeys.MakeCreationOptionsAsync(new PasskeyUserEntity { Id = user.Id.ToString(), Name = user.UserName, DisplayName = user.DisplayName }, http);
        return TypedResults.Ok(new PasskeyOptionsResponse(JsonElement.Parse(options.CreationOptionsJson), state.Protect(user.TenantId, user.Id, options.AttestationState)));
    }

    private static async Task<Results<Created<PasskeyResponse>, ValidationProblem, ProblemHttpResult>> RegisterAsync(
        PasskeyRegistrationRequest request, HttpContext http, Caller caller, PasskeyTenant scope, IdentityDbContext db, UserManager<User> users,
        IPasskeyHandler<User> passkeys, PasskeyState state, CancellationToken cancellationToken)
    {
        if (await CurrentAsync(caller, scope, db, cancellationToken) is not { } user)
        {
            return ApiErrors.NotFound();
        }

        if (!state.TryUnprotect(request.State, user.TenantId, user.Id, out var attestationState))
        {
            return ApiErrors.Validation("state", "The registration expired. Request new options.");
        }

        var result = await passkeys.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = http,
            CredentialJson = request.Credential.GetRawText(),
            AttestationState = attestationState,
        });
        if (!result.Succeeded || result.UserEntity.Id != user.Id.ToString())
        {
            return ApiErrors.Validation("credential", result.Failure?.Message ?? "The passkey could not be verified.");
        }

        var passkey = result.Passkey;
        passkey.Name = request.Name?.Trim() is { Length: > 0 } name ? name : "Passkey";
        var saved = await users.AddOrUpdatePasskeyAsync(user, passkey);
        if (!saved.Succeeded)
        {
            return ApiErrors.Validation("credential", string.Join(" ", saved.Errors.Select(e => e.Description)));
        }

        var response = ToResponse(passkey);
        return TypedResults.Created($"/v1.0/me/passkeys/{response.Id}", response);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        string id, Caller caller, PasskeyTenant scope, IdentityDbContext db, UserManager<User> users, CancellationToken cancellationToken)
    {
        byte[] credentialId;
        try
        {
            credentialId = Base64Url.DecodeFromChars(id);
        }
        catch (FormatException)
        {
            return ApiErrors.NotFound();
        }

        if (await CurrentAsync(caller, scope, db, cancellationToken) is not { } user || await users.GetPasskeyAsync(user, credentialId) is null)
        {
            return ApiErrors.NotFound();
        }

        await users.RemovePasskeyAsync(user, credentialId);
        return TypedResults.NoContent();
    }

    private static async Task<User?> CurrentAsync(Caller caller, PasskeyTenant scope, IdentityDbContext db, CancellationToken cancellationToken)
    {
        scope.TenantId = caller.TenantId;
        return await Users.FindAsync(db, caller.TenantId, caller.UserId, cancellationToken) is { } user && SignInSession.CanSignIn(user) ? user : null;
    }

    private static PasskeyResponse ToResponse(UserPasskeyInfo p) => new(Base64Url.EncodeToString(p.CredentialId), p.Name ?? "Passkey", p.CreatedAt, p.IsBackedUp);
}
