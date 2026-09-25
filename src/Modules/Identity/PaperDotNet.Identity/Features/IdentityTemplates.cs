using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Identity.Features;

/// <summary>Template section <c>Groups</c> (PRV-01/02): groups and their members by user name; additive.</summary>
internal sealed class GroupTemplateHandler(IdentityDbContext db, ITenantContext tenant, HybridCache cache) : ITemplateHandler
{
    public XName Element => TemplateXml.Name("Groups");

    public TemplateLevel Level => TemplateLevel.Tenant;

    public int Order => 200;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var groups = (await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(cancellationToken))
            .Where(g => context.Includes(TemplateKinds.Group, g.Name)).ToList();
        if (groups.Count == 0)
        {
            return null;
        }

        var groupIds = groups.Select(g => g.Id).ToList();
        var members = await db.GroupMembers.Where(m => groupIds.Contains(m.GroupId))
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.GroupId, u.UserName })
            .ToListAsync(cancellationToken);
        return new XElement(Element, groups.Select(g => new XElement(TemplateXml.Name("Group"),
            members.Where(m => m.GroupId == g.Id && m.UserName != null).OrderBy(m => m.UserName, StringComparer.Ordinal)
                .Select(m => new XElement(TemplateXml.Name("Member"), new XAttribute("User", m.UserName!))))
            .With("Name", g.Name).With("Description", g.Description)));
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var element in section.Elements(TemplateXml.Name("Group")))
        {
            var name = element.RequiredAttr("Name").Trim();
            var description = element.Attr("Description");
            var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == name, cancellationToken);
            if (group is null)
            {
                group = new Group { Id = Ids.New(), Name = name, Description = description };
                context.Created(TemplateKinds.Group, name);
                if (!context.DryRun)
                {
                    db.Groups.Add(group);
                    changed = true;
                }
            }
            else if (group.Description != description)
            {
                context.Updated(TemplateKinds.Group, name, "description");
                group.Description = description;
                changed = true;
            }

            context.Register(TemplateKinds.Group, name, group.Id);
            var existing = (await db.GroupMembers.Where(m => m.GroupId == group.Id).Select(m => m.UserId).ToListAsync(cancellationToken)).ToHashSet();
            var added = new List<string>();
            foreach (var member in element.Elements(TemplateXml.Name("Member")))
            {
                var userName = member.RequiredAttr("User");
                var userId = await IdentityTemplateLookups.FindUserAsync(db, userName, cancellationToken);
                if (userId is null)
                {
                    context.Warn($"User '{userName}' does not exist; not added to group '{name}'.", member);
                }
                else if (existing.Add(userId.Value))
                {
                    added.Add(userName);
                    if (!context.DryRun)
                    {
                        db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = userId.Value });
                        changed = true;
                    }
                }
            }

            if (added.Count > 0)
            {
                context.Updated(TemplateKinds.Group, name, "members added: " + string.Join(", ", added));
            }
        }

        if (changed && !context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
            await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(tenant.TenantId!.Value), cancellationToken);
        }
    }
}

/// <summary>
/// Template section <c>Roles</c>: custom roles (built-in ones are not exported or changed), their scopes
/// and assignments to users and groups by name; additive.
/// </summary>
internal sealed class RoleTemplateHandler(IdentityDbContext db, IScopeCatalog catalog, ITenantContext tenant, HybridCache cache) : ITemplateHandler
{
    public XName Element => TemplateXml.Name("Roles");

    public TemplateLevel Level => TemplateLevel.Tenant;

    public int Order => 210;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        if (context.Scope != TemplateScope.Tenant)
        {
            return null;
        }

        var roles = await db.Roles.AsNoTracking().Where(r => !r.IsBuiltIn).OrderBy(r => r.Name).ToListAsync(cancellationToken);
        if (roles.Count == 0)
        {
            return null;
        }

        var roleIds = roles.Select(r => r.Id).ToList();
        var assignments = await db.RoleAssignments.AsNoTracking().Where(a => roleIds.Contains(a.RoleId)).ToListAsync(cancellationToken);
        var principalIds = assignments.Select(a => a.PrincipalId).ToList();
        var users = await db.Users.Where(u => principalIds.Contains(u.Id) && u.UserName != null).ToDictionaryAsync(u => u.Id, u => u.UserName!, cancellationToken);
        var groups = await db.Groups.Where(g => principalIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        return new XElement(Element, roles.Select(role => new XElement(TemplateXml.Name("Role"),
            role.Scopes.Order(StringComparer.Ordinal).Select(s => new XElement(TemplateXml.Name("Scope"), s)),
            assignments.Where(a => a.RoleId == role.Id)
                .Select(a => a.PrincipalType == PrincipalType.User
                    ? users.TryGetValue(a.PrincipalId, out var user) ? new XElement(TemplateXml.Name("Assignment"), new XAttribute("User", user)) : null
                    : groups.TryGetValue(a.PrincipalId, out var group) ? new XElement(TemplateXml.Name("Assignment"), new XAttribute("Group", group)) : null)
                .OfType<XElement>()
                .OrderBy(e => e.ToString(), StringComparer.Ordinal))
            .With("Name", role.Name).With("Description", role.Description)));
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var element in section.Elements(TemplateXml.Name("Role")))
        {
            var name = element.RequiredAttr("Name").Trim();
            var scopes = element.Elements(TemplateXml.Name("Scope")).Select(s => s.Value.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var unknown = scopes.Where(s => !catalog.Contains(s)).ToList();
            if (unknown.Count > 0)
            {
                throw new TemplateException($"Role '{name}': unknown scopes {string.Join(", ", unknown)}.", element);
            }

            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == name, cancellationToken);
            if (role is { IsBuiltIn: true })
            {
                context.Warn($"Role '{name}' is built in and was not changed.", element);
                continue;
            }

            if (role is null)
            {
                role = new Role { Id = Ids.New(), Name = name, Description = element.Attr("Description"), Scopes = scopes };
                context.Created(TemplateKinds.Role, name);
                if (!context.DryRun)
                {
                    db.Roles.Add(role);
                    changed = true;
                }
            }
            else
            {
                var missing = scopes.Where(s => !role.Scopes.Contains(s)).ToList();
                if (missing.Count > 0 || role.Description != element.Attr("Description"))
                {
                    context.Updated(TemplateKinds.Role, name, missing.Count > 0 ? "scopes added: " + string.Join(", ", missing) : "description");
                    role.Scopes = [.. role.Scopes, .. missing];
                    role.Description = element.Attr("Description");
                    changed = true;
                }
            }

            var existing = (await db.RoleAssignments.Where(a => a.RoleId == role.Id).Select(a => a.PrincipalId).ToListAsync(cancellationToken)).ToHashSet();
            foreach (var assignment in element.Elements(TemplateXml.Name("Assignment")))
            {
                var (type, principal, label) = await IdentityTemplateLookups.PrincipalAsync(db, assignment, context, cancellationToken);
                if (principal is null)
                {
                    context.Warn($"{label} does not exist; not assigned to role '{name}'.", assignment);
                }
                else if (existing.Add(principal.Value))
                {
                    context.Updated(TemplateKinds.Role, name, $"assigned to {label}");
                    if (!context.DryRun)
                    {
                        db.RoleAssignments.Add(new RoleAssignment { Id = Ids.New(), RoleId = role.Id, PrincipalId = principal.Value, PrincipalType = type });
                        changed = true;
                    }
                }
            }
        }

        if (changed && !context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
            await cache.RemoveByTagAsync(EffectiveScopeProvider.TenantTag(tenant.TenantId!.Value), cancellationToken);
        }
    }
}

internal static class IdentityTemplateLookups
{
    public static async Task<Guid?> FindUserAsync(IdentityDbContext db, string userName, CancellationToken ct)
    {
        var normalized = userName.Trim().ToUpperInvariant();
        return await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>A <c>User</c> or <c>Group</c> attribute: groups of the same template count even before they exist (dry run).</summary>
    public static async Task<(PrincipalType Type, Guid? Id, string Label)> PrincipalAsync(IdentityDbContext db, XElement element, TemplateContext context, CancellationToken ct)
    {
        if (element.Attr("Group") is { } group)
        {
            var id = context.Resolve(TemplateKinds.Group, group)
                ?? await db.Groups.Where(g => g.Name == group).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct);
            return (PrincipalType.Group, id, $"Group '{group}'");
        }

        if (element.Attr("User") is { } user)
        {
            return (PrincipalType.User, await FindUserAsync(db, user, ct), $"User '{user}'");
        }

        throw new TemplateException($"{element.Name.LocalName}: a User or Group attribute is required.", element);
    }
}
