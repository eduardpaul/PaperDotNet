using System.Net;
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
/// Sign-in through an authenticating reverse proxy (IAM-15). Only requests whose direct peer is a trusted proxy count,
/// and only at <c>/connect/authorize</c>: it is a GET with OAuth state and PKCE, so cross-site requests cannot use the
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
    /// <summary>
    /// The user the proxy names, found or created, with name, e-mail and groups updated; null when the request does not
    /// come from a trusted proxy, names nobody, or the user may not sign in.
    /// </summary>
    public async Task<User?> AuthenticateAsync(HttpContext http, CancellationToken ct)
    {
        var settings = options.Value.ReverseProxy;
        if (!settings.Enabled || !IsTrusted(PeerAddress.Get(http), settings))
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

    /// <summary>Checks the options at startup: trusted proxies are required and must parse.</summary>
    public static bool IsValid(ReverseProxyAuthOptions settings) =>
        !settings.Enabled
        || (settings.TrustedProxies.Count > 0
            && settings.TrustedProxies.All(p => IPNetwork.TryParse(p, out _) || IPAddress.TryParse(p, out _))
            && !string.IsNullOrWhiteSpace(settings.UserHeader));

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

    private static string? Header(HttpContext http, string? name) =>
        name is null || http.Request.Headers[name].ToString().Trim() is not { Length: > 0 and <= 256 } value ? null : value;

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

    /// <summary>Takes over name and e-mail, and adds the user to existing groups the proxy names.</summary>
    private async Task UpdateAsync(User user, HttpContext http, ReverseProxyAuthOptions settings, CancellationToken ct)
    {
        var changed = false;
        var joined = false;
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

        var groupNames = (Header(http, settings.GroupsHeader) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (groupNames.Length > 0)
        {
            var groups = await db.Groups.Where(g => groupNames.Contains(g.Name)).Select(g => g.Id).ToListAsync(ct);
            var current = await db.GroupMembers.Where(m => m.UserId == user.Id).Select(m => m.GroupId).ToListAsync(ct);
            var added = groups.Except(current).ToList();
            db.GroupMembers.AddRange(added.Select(g => new GroupMember { GroupId = g, UserId = user.Id }));
            joined = added.Count > 0;
        }

        if (changed || joined)
        {
            await db.SaveChangesAsync(ct);
        }

        if (joined)
        {
            await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(user.TenantId), ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reverse proxy named unknown user {UserName}; creating users is off.")]
    private partial void LogUnknownUser(string userName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create user {UserName} from the reverse proxy: {Errors}")]
    private partial void LogCreateFailed(string userName, string errors);
}
