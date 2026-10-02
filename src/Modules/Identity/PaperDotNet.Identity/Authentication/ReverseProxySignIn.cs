using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Identity.Data;
using PaperDotNet.Identity.Features;

namespace PaperDotNet.Identity.Authentication;

/// <summary>
/// Sign-in through an authenticating reverse proxy (IAM-15, ADR-0031, ADR-0043). Only requests whose direct peer is a
/// trusted proxy count, with the proxy's secret when one is configured, and only at <c>/auth/proxy/sign-in</c> and
/// <c>/connect/authorize</c>: both only start a sign-in session for the visitor, so cross-site requests cannot use the
/// proxy's cookie to act on the API. The API itself keeps using tokens.
/// </summary>
internal sealed partial class ReverseProxySignIn(
    IOptions<AuthOptions> options,
    UserManager<User> users,
    IdentityDbContext db,
    ITenantContext tenant,
    HybridCache cache,
    ILogger<ReverseProxySignIn> logger)
{
    /// <summary>The browser path where the proxy signs people in (ADR-0043), outside <c>/connect/</c>.</summary>
    public const string SignInPath = "/auth/proxy/sign-in";

    /// <summary>Authentication method of sign-in sessions the proxy started.</summary>
    public const string Method = "proxy";

    /// <summary>When the proxy signed the user in (Unix seconds), in the sign-in session, authorization codes and refresh tokens.</summary>
    public const string SignedInAtClaim = "proxy_at";

    /// <summary>When the proxy signed in the user of this session or grant; null when the proxy did not.</summary>
    public static DateTimeOffset? SignedInAt(ClaimsPrincipal? principal) =>
        principal?.FindFirst(SignedInAtClaim)?.Value is { } value && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    public static Claim SignedInAtClaimFor(DateTimeOffset signedInAt) =>
        new(SignedInAtClaim, signedInAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The user the proxy names, found or created, with name, e-mail and groups updated; null when the request does not
    /// come from a trusted proxy, names nobody, or the user may not sign in.
    /// </summary>
    public async Task<User?> AuthenticateAsync(HttpContext http, CancellationToken ct)
    {
        var settings = options.Value.ReverseProxy;
        if (!settings.Enabled || !IsTrusted(PeerAddress.Get(http), settings) || !HasSecret(http, settings))
        {
            return null;
        }

        var userName = Header(http, settings.UserHeader);
        if (userName is null)
        {
            return null;
        }

        var user = await users.FindByNameAsync(userName);
        if (user is null)
        {
            if (!settings.CreateUsers)
            {
                LogUnknownUser(userName);
                return null;
            }

            user = await CreateAsync(userName, ct);
            if (user is null)
            {
                return null;
            }
        }

        if (!OAuthEndpoints.CanSignIn(user))
        {
            return null;
        }

        await UpdateAsync(user, http, settings, ct);
        return user;
    }

    /// <summary>
    /// Checks the options at startup: trusted proxies are required and must parse, a secret must be long enough and
    /// a sign-in must last a while.
    /// </summary>
    public static bool IsValid(ReverseProxyAuthOptions settings) =>
        !settings.Enabled
        || (settings.TrustedProxies.Count > 0
            && settings.TrustedProxies.All(p => IPNetwork.TryParse(p, out _) || IPAddress.TryParse(p, out _))
            && !string.IsNullOrWhiteSpace(settings.UserHeader)
            && (string.IsNullOrEmpty(settings.Secret)
                || (settings.Secret.Length >= ReverseProxyAuthOptions.MinSecretLength && !string.IsNullOrWhiteSpace(settings.SecretHeader)))
            && settings.RefreshTokenLifetime >= TimeSpan.FromMinutes(5)
            && (string.IsNullOrEmpty(settings.LogoutUrl) || Uri.TryCreate(settings.LogoutUrl, UriKind.Absolute, out _)));

    /// <summary>The proxy's secret, compared in constant time; true when none is configured.</summary>
    private static bool HasSecret(HttpContext http, ReverseProxyAuthOptions settings)
    {
        if (string.IsNullOrEmpty(settings.Secret))
        {
            return true;
        }

        var sent = http.Request.Headers[settings.SecretHeader].ToString();
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(sent)),
            SHA256.HashData(Encoding.UTF8.GetBytes(settings.Secret)));
    }

    private static bool IsTrusted(IPAddress? peer, ReverseProxyAuthOptions settings)
    {
        if (peer is null)
        {
            return false;
        }

        peer = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        foreach (var entry in settings.TrustedProxies)
        {
            if (IPNetwork.TryParse(entry, out var network) ? network.Contains(peer) : IPAddress.TryParse(entry, out var address) && address.Equals(peer))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Header(HttpContext http, string? name, int maxLength = 256) =>
        name is null || http.Request.Headers[name].ToString().Trim() is not { Length: > 0 } value || value.Length > maxLength ? null : value;

    private async Task<User?> CreateAsync(string userName, CancellationToken ct)
    {
        var user = new User { Id = Ids.New(), TenantId = tenant.TenantId!.Value, UserName = userName, DisplayName = userName };
        var result = await users.CreateAsync(user);
        if (!result.Succeeded)
        {
            LogCreateFailed(userName, string.Join(" ", result.Errors.Select(e => e.Description)));
            return null;
        }

        var member = await db.Roles.Where(r => r.IsBuiltIn && r.Name == Role.Member).Select(r => r.Id).FirstAsync(ct);
        db.RoleAssignments.Add(new RoleAssignment { Id = Ids.New(), RoleId = member, PrincipalId = user.Id, PrincipalType = PrincipalType.User });
        await db.SaveChangesAsync(ct);
        await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(user.TenantId), ct);
        return user;
    }

    /// <summary>Takes over name and e-mail, and the groups the proxy names.</summary>
    private async Task UpdateAsync(User user, HttpContext http, ReverseProxyAuthOptions settings, CancellationToken ct)
    {
        var changed = false;
        if (Header(http, settings.NameHeader) is { } name && name != user.DisplayName)
        {
            user.DisplayName = name.Length > 200 ? name[..200] : name;
            changed = true;
        }

        if (Header(http, settings.EmailHeader) is { } email && email != user.Email)
        {
            user.Email = email;
            user.NormalizedEmail = users.NormalizeEmail(email);
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        if (await UpdateGroupsAsync(user, http, settings, ct))
        {
            await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(user.TenantId), ct);
        }
    }

    /// <summary>
    /// Adds the user to the groups the proxy names (creating missing ones with <see cref="ReverseProxyAuthOptions.CreateGroups"/>)
    /// and, with <see cref="ProxyGroupSync.Sync"/>, removes them from proxy groups it no longer names. Local groups only
    /// ever gain members, and the last administrator is never removed. True when memberships changed.
    /// </summary>
    private async Task<bool> UpdateGroupsAsync(User user, HttpContext http, ReverseProxyAuthOptions settings, CancellationToken ct)
    {
        var names = (Header(http, settings.GroupsHeader, MaxGroupsHeaderLength) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length <= MaxGroupNameLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxGroups)
            .ToList();
        if (names.Count == 0 && settings.GroupSync == ProxyGroupSync.Add)
        {
            return false;
        }

        var named = await db.Groups.Where(g => names.Contains(g.Name)).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
        var namedIds = named.Select(g => g.Id).ToHashSet();
        if (settings.CreateGroups)
        {
            foreach (var name in names.Except(named.Select(g => g.Name), StringComparer.Ordinal))
            {
                if (await CreateGroupAsync(name, ct) is { } id)
                {
                    namedIds.Add(id);
                }
            }
        }

        var current = await db.GroupMembers.Where(m => m.UserId == user.Id).Select(m => m.GroupId).ToListAsync(ct);
        var added = namedIds.Except(current).ToList();
        db.GroupMembers.AddRange(added.Select(g => new GroupMember { GroupId = g, UserId = user.Id }));
        if (added.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var removed = 0;
        if (settings.GroupSync == ProxyGroupSync.Sync)
        {
            var stale = await db.Groups
                .Where(g => current.Contains(g.Id) && g.Source == GroupSource.Proxy && !namedIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Name })
                .ToListAsync(ct);
            foreach (var group in stale)
            {
                // One at a time, so each check sees the memberships removed before it.
                if (!await AdministratorGuard.RemainsAsync(db, withoutMembership: (group.Id, user.Id), ct: ct))
                {
                    LogKeptLastAdministrator(user.UserName ?? user.Id.ToString(), group.Name);
                    continue;
                }

                db.GroupMembers.Remove(await db.GroupMembers.FirstAsync(m => m.GroupId == group.Id && m.UserId == user.Id, ct));
                await db.SaveChangesAsync(ct);
                removed++;
            }
        }

        return added.Count > 0 || removed > 0;
    }

    /// <summary>A new proxy group; null when its name is taken meanwhile (another sign-in created it first).</summary>
    private async Task<Guid?> CreateGroupAsync(string name, CancellationToken ct)
    {
        var group = new Group { Id = Ids.New(), Name = name, Source = GroupSource.Proxy };
        db.Groups.Add(group);
        try
        {
            await db.SaveChangesAsync(ct);
            LogGroupCreated(name);
            return group.Id;
        }
        catch (DbUpdateException)
        {
            db.Entry(group).State = EntityState.Detached;
            return await db.Groups.Where(g => g.Name == name).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct);
        }
    }

    private const int MaxGroupsHeaderLength = 8192;
    private const int MaxGroupNameLength = 200;
    private const int MaxGroups = 100;

    [LoggerMessage(Level = LogLevel.Information, Message = "Reverse proxy named unknown user {UserName}; creating users is off.")]
    private partial void LogUnknownUser(string userName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create user {UserName} from the reverse proxy: {Errors}")]
    private partial void LogCreateFailed(string userName, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created group {GroupName} named by the reverse proxy.")]
    private partial void LogGroupCreated(string groupName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kept {UserName} in proxy group {GroupName}: removing them would leave no administrator.")]
    private partial void LogKeptLastAdministrator(string userName, string groupName);
}
