using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Content;
using PaperDotNet.Identity.Contracts;

namespace PaperDotNet.Host.Content;

internal static class ContentScopes
{
    public const string Read = "content.read";
    public const string Draft = "content.read_draft";
    public const string Write = "content.write";
    public const string Publish = "content.publish";
    public const string Manage = "content.manage";
    public static readonly ScopeDefinition[] All = [new(Read, "Read published content", true), new(Draft, "Read drafts", true),
        new(Write, "Edit content", true), new(Publish, "Publish content", true), new(Manage, "Manage content collections and grants", true)];
}

internal sealed class ContentAccess(ContentDbContext db, IUserDirectory directory, ICurrentUser current,
    IHttpContextAccessor http, IAuthorizationService authorization) : IContentAuthorization
{
    private static string Scope(string action) => action switch
    { "read" => ContentScopes.Read, "read_draft" => ContentScopes.Draft, "write" => ContentScopes.Write, "publish" => ContentScopes.Publish, _ => ContentScopes.Manage };

    public async Task AuthorizeAsync(string actor, ResourcesRow resource, string action, CancellationToken cancellationToken)
    {
        if (current.UserId?.ToString() != actor || http.HttpContext is not { } context
            || !(await authorization.AuthorizeAsync(context.User, null, ScopePolicyProvider.Prefix + Scope(action))).Succeeded)
            throw new UnauthorizedAccessException("The action is not permitted.");
        if ((await authorization.AuthorizeAsync(context.User, null, ScopePolicyProvider.Prefix + "organization.manage")).Succeeded) return;
        if (resource.Kind == "workspace" && action == "manage" && !await db.Resources.AnyAsync(x => x.Id == resource.Id, cancellationToken)) return;
        var groups = (await directory.GetGroupIdsAsync(current.UserId.Value, cancellationToken)).Select(x => x.ToString()).ToArray();
        var principals = db.Set<GrantsRow>().Where(x => x.PrincipalType == "user" && x.PrincipalId == actor
            || x.PrincipalType == "group" && groups.Contains(x.PrincipalId));
        // Workspace owners retain management rights over uniquely secured children.
        if (await principals.AnyAsync(x => x.ResourceId == resource.WorkspaceId && x.Action == "manage", cancellationToken)) return;
        var scope = resource.ScopeId ?? resource.Id;
        if (await principals.AnyAsync(x => x.ResourceId == scope && (x.Action == action || x.Action == "manage"), cancellationToken)) return;
        throw new UnauthorizedAccessException("The action is not permitted on this resource.");
    }

    public async Task<bool> AllowedAsync(string actor, ResourcesRow resource, string action, CancellationToken cancellationToken)
    {
        try { await AuthorizeAsync(actor, resource, action, cancellationToken); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
