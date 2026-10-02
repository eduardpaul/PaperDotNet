using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Data;
using PaperDotNet.Identity.Features;

namespace PaperDotNet.Identity.Authentication;

/// <summary>
/// Configuration section <c>Auth:ReverseProxy</c> (IAM-15): an authenticating proxy (Authelia, Authentik, oauth2-proxy)
/// names the signed-in user in request headers. They are trusted only from <see cref="TrustedProxies"/>, and only at
/// <c>/connect/authorize</c>, which then starts a sign-in session; the API itself keeps using tokens.
/// </summary>
public sealed class ReverseProxyAuthOptions
{
    public bool Enabled { get; set; }

    /// <summary>Addresses or networks (CIDR) of the proxies whose headers are trusted, e.g. <c>172.18.0.0/16</c>. Required when enabled.</summary>
    public List<string> TrustedProxies { get; set; } = [];

    public string UserHeader { get; set; } = "Remote-User";

    public string? EmailHeader { get; set; } = "Remote-Email";

    public string? NameHeader { get; set; } = "Remote-Name";

    /// <summary>Comma-separated group names; users are added to existing groups of those names (never removed).</summary>
    public string? GroupsHeader { get; set; } = "Remote-Groups";

    /// <summary>Create unknown users (without a password, with the member role) on their first sign-in.</summary>
    public bool CreateUsers { get; set; } = true;
}

/// <summary>
/// Sign-in through an authenticating reverse proxy (IAM-15). Only requests whose direct peer is a trusted proxy count,
/// and only at <c>/connect/authorize</c>: it is a GET with OAuth state and PKCE, so cross-site requests cannot use the
/// proxy's cookie to act on the API.
/// </summary>
internal sealed partial class ReverseProxySignIn(IOptions<AuthOptions> options, IdentityDbContext db, IUserDirectory directory, ILogger<ReverseProxySignIn> logger)
{
    /// <summary>
    /// The user of the tenant the proxy names, found or created, with name, e-mail and groups updated; null when the
    /// request does not come from a trusted proxy, names nobody, or the user may not sign in.
    /// </summary>
    public async Task<User?> AuthenticateAsync(HttpContext http, Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = options.Value.ReverseProxy;
        if (!settings.Enabled || !IsTrusted(http.Connection.RemoteIpAddress, settings) || Header(http, settings.UserHeader) is not { } userName)
        {
            return null;
        }

        var user = await FindAsync(tenantId, userName, cancellationToken);
        if (user is null)
        {
            if (!settings.CreateUsers)
            {
                LogUnknownUser(userName);
                return null;
            }

            try
            {
                var id = await directory.CreateUserAsync(tenantId, new NewUser(userName, null, Header(http, settings.NameHeader), null), cancellationToken);
                user = await Users.FindAsync(db, tenantId, id, cancellationToken);
            }
            catch (UserCreationException ex)
            {
                LogCreateFailed(userName, string.Join(" ", ex.Errors));
                return null;
            }
        }

        if (user is null || !SignInSession.CanSignIn(user))
        {
            return null;
        }

        await UpdateAsync(user, http, settings, cancellationToken);
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
        return settings.TrustedProxies.Any(entry =>
            IPNetwork.TryParse(entry, out var network) ? network.Contains(peer) : IPAddress.TryParse(entry, out var address) && address.Equals(peer));
    }

    private static string? Header(HttpContext http, string? name) =>
        name is null || http.Request.Headers[name].ToString().Trim() is not { Length: > 0 and <= 256 } value ? null : value;

    private Task<User?> FindAsync(Guid tenantId, string userName, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = tenantId;
        var normalized = Users.Normalize(userName);
        var ct = cancellationToken;
        return database.Users.Where(u => u.TenantId == tenant && u.NormalizedUserName == normalized && u.DeletedAt == null).FirstOrDefaultAsync(ct);
    }

    /// <summary>Takes over name and e-mail, and adds the user to existing groups the proxy names.</summary>
    private async Task UpdateAsync(User user, HttpContext http, ReverseProxyAuthOptions settings, CancellationToken cancellationToken)
    {
        var changed = false;
        if (Header(http, settings.NameHeader) is { } name && name != user.DisplayName)
        {
            user.DisplayName = name;
            changed = true;
        }

        if (Header(http, settings.EmailHeader) is { } header && Users.TryEmail(header, out var email) && email != user.Email)
        {
            user.Email = email;
            changed = true;
        }

        var database = db;
        var tenant = user.TenantId;
        var userId = user.Id;
        var ct = cancellationToken;
        foreach (var groupName in (Header(http, settings.GroupsHeader) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal))
        {
            var group = groupName;
            var groupId = await database.Groups.Where(g => g.TenantId == tenant && g.Name == group).Select(g => g.Id).FirstOrDefaultAsync(ct);
            if (groupId != Guid.Empty && !await database.GroupMembers.AnyAsync(m => m.TenantId == tenant && m.GroupId == groupId && m.UserId == userId, ct))
            {
                database.GroupMembers.Add(new GroupMember { TenantId = tenant, GroupId = groupId, UserId = userId });
                changed = true;
            }
        }

        if (changed)
        {
            await database.SaveChangesAsync(ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The reverse proxy named the unknown user {UserName}; creating users is off.")]
    private partial void LogUnknownUser(string userName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create the user {UserName} from the reverse proxy: {Errors}")]
    private partial void LogCreateFailed(string userName, string errors);
}
