using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Data;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Identity.Features;

/// <summary>
/// Template section <c>Groups</c> (PRV-01/02): groups and their members by user name, and the groups inside them by
/// name (<c>&lt;Member Group="…"/&gt;</c>, ADR-0035); additive. Closures and cached access follow from the save
/// (<see cref="IdentityDbContext"/>).
/// </summary>
internal sealed class GroupTemplateHandler(IdentityDbContext db) : ITemplateHandler
{
    public XName Element => TemplateXml.Name("Groups");

    public TemplateLevel Level => TemplateLevel.Tenant;

    public int Order => 200;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        var ct = cancellationToken;
        var all = await database.Groups.AsNoTracking().Where(g => g.TenantId == tenant).OrderBy(g => g.Name).ToListAsync(ct);
        var nestings = await database.GroupNestings.AsNoTracking().Where(n => n.TenantId == tenant).ToListAsync(ct);
        var inside = nestings.ToLookup(n => n.GroupId, n => n.MemberGroupId);

        // Groups the template needs, and the groups inside them (their members count as members too).
        var included = new HashSet<Guid>();
        var pending = new Stack<Guid>(all.Where(g => context.Includes(TemplateKinds.Group, g.Name)).Select(g => g.Id));
        while (pending.TryPop(out var id))
        {
            if (included.Add(id))
            {
                foreach (var member in inside[id])
                {
                    pending.Push(member);
                }
            }
        }

        var groups = all.Where(g => included.Contains(g.Id)).ToList();
        if (groups.Count == 0)
        {
            return null;
        }

        var members = await database.GroupMembers.AsNoTracking().Where(m => m.TenantId == tenant).ToListAsync(ct);
        var users = (await database.Users.AsNoTracking().Where(u => u.TenantId == tenant && u.DeletedAt == null).ToListAsync(ct)).ToDictionary(u => u.Id, u => u.UserName);
        var names = all.ToDictionary(g => g.Id, g => g.Name);
        return new XElement(Element, groups.Select(g => new XElement(TemplateXml.Name("Group"),
            members.Where(m => m.GroupId == g.Id && users.ContainsKey(m.UserId)).Select(m => users[m.UserId]).Order(StringComparer.Ordinal)
                .Select(u => new XElement(TemplateXml.Name("Member"), new XAttribute("User", u))),
            inside[g.Id].Where(names.ContainsKey).Select(id => names[id]).Order(StringComparer.Ordinal)
                .Select(n => new XElement(TemplateXml.Name("Member"), new XAttribute("Group", n))))
            .With("Name", g.Name).With("Description", g.Description)));
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var tenantId = context.TenantId;
        foreach (var element in section.Elements(TemplateXml.Name("Group")))
        {
            var name = element.RequiredAttr("Name").Trim();
            var description = element.Attr("Description");
            var group = await IdentityTemplateLookups.GroupAsync(db, tenantId, name, cancellationToken);
            var created = group is null;
            if (group is null)
            {
                group = new Group { Id = Ids.New(), TenantId = tenantId, Name = name, Description = description };
                context.Created(TemplateKinds.Group, name);
                if (!context.DryRun)
                {
                    db.Groups.Add(group);
                }
            }
            else if (group.Description != description)
            {
                context.Updated(TemplateKinds.Group, name, "description");
                group.Description = description;
            }

            context.Register(TemplateKinds.Group, name, group.Id);
            var existing = created ? [] : await IdentityTemplateLookups.MemberIdsAsync(db, tenantId, group.Id, cancellationToken);
            var added = new List<string>();
            foreach (var member in element.Elements(TemplateXml.Name("Member")).Where(m => m.Attr("Group") is null))
            {
                var userName = member.RequiredAttr("User");
                var userId = await IdentityTemplateLookups.UserAsync(db, tenantId, userName, cancellationToken);
                if (userId is null)
                {
                    context.Warn($"User '{userName}' does not exist; not added to group '{name}'.", member);
                }
                else if (existing.Add(userId.Value))
                {
                    added.Add(userName);
                    if (!context.DryRun)
                    {
                        db.GroupMembers.Add(new GroupMember { TenantId = tenantId, GroupId = group.Id, UserId = userId.Value });
                    }
                }
            }

            if (added.Count > 0)
            {
                context.Updated(TemplateKinds.Group, name, "members added: " + string.Join(", ", added));
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        // Groups inside groups, once every group of the section exists.
        await ApplyNestingsAsync(section, context, cancellationToken);
    }

    private async Task ApplyNestingsAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var database = db;
        var tenant = context.TenantId;
        var ct = cancellationToken;
        var nestings = (await database.GroupNestings.AsNoTracking().Where(n => n.TenantId == tenant).Select(n => new NestingPair(n.GroupId, n.MemberGroupId)).ToListAsync(ct))
            .Select(n => (n.GroupId, n.MemberGroupId))
            .ToList();
        var changed = false;
        foreach (var element in section.Elements(TemplateXml.Name("Group")))
        {
            var name = element.RequiredAttr("Name").Trim();
            foreach (var member in element.Elements(TemplateXml.Name("Member")))
            {
                if (member.Attr("Group") is not { } memberName)
                {
                    continue;
                }

                var groupId = context.Resolve(TemplateKinds.Group, name);
                var memberId = context.Resolve(TemplateKinds.Group, memberName) ?? (await IdentityTemplateLookups.GroupAsync(db, tenant, memberName, ct))?.Id;
                if (memberId is null)
                {
                    context.Warn($"Group '{memberName}' does not exist; not added to group '{name}'.", member);
                    continue;
                }

                if (groupId is null || nestings.Contains((groupId.Value, memberId.Value)))
                {
                    continue;
                }

                if (GroupGraph.CheckNesting(groupId.Value, memberId.Value, nestings) is { } problem)
                {
                    throw new TemplateException($"Group '{memberName}' cannot go into group '{name}': {problem}", member);
                }

                context.Updated(TemplateKinds.Group, name, $"group added: {memberName}");
                nestings.Add((groupId.Value, memberId.Value));
                if (!context.DryRun)
                {
                    db.GroupNestings.Add(new GroupNesting { TenantId = tenant, GroupId = groupId.Value, MemberGroupId = memberId.Value });
                    changed = true;
                }
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>
/// Template section <c>Roles</c>: custom roles (built-in ones are not exported or changed), their scopes and assignments
/// to users and groups by name; additive. Tenant templates only.
/// </summary>
internal sealed class RoleTemplateHandler(IdentityDbContext db, IScopeCatalog catalog) : ITemplateHandler
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

        var database = db;
        var tenant = context.TenantId;
        var ct = cancellationToken;
        var roles = await database.Roles.AsNoTracking().Where(r => r.TenantId == tenant && !r.IsBuiltIn).OrderBy(r => r.Name).ToListAsync(ct);
        if (roles.Count == 0)
        {
            return null;
        }

        var assignments = await database.RoleAssignments.AsNoTracking().Where(a => a.TenantId == tenant).ToListAsync(ct);
        var users = (await database.Users.AsNoTracking().Where(u => u.TenantId == tenant && u.DeletedAt == null).ToListAsync(ct)).ToDictionary(u => u.Id, u => u.UserName);
        var groups = (await database.Groups.AsNoTracking().Where(g => g.TenantId == tenant).ToListAsync(ct)).ToDictionary(g => g.Id, g => g.Name);
        return new XElement(Element, roles.Select(role => new XElement(TemplateXml.Name("Role"),
            ScopeList.Parse(role.Scopes).Order(StringComparer.Ordinal).Select(s => new XElement(TemplateXml.Name("Scope"), s)),
            assignments.Where(a => a.RoleId == role.Id)
                .Select(a => a.PrincipalType == PrincipalTypes.User
                    ? users.TryGetValue(a.PrincipalId, out var user) ? new XElement(TemplateXml.Name("Assignment"), new XAttribute("User", user)) : null
                    : groups.TryGetValue(a.PrincipalId, out var group) ? new XElement(TemplateXml.Name("Assignment"), new XAttribute("Group", group)) : null)
                .OfType<XElement>()
                .OrderBy(e => e.ToString(), StringComparer.Ordinal))
            .With("Name", role.Name).With("Description", role.Description)));
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var tenantId = context.TenantId;
        foreach (var element in section.Elements(TemplateXml.Name("Role")))
        {
            var name = element.RequiredAttr("Name").Trim();
            var scopes = element.Elements(TemplateXml.Name("Scope")).Select(s => s.Value.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var unknown = scopes.Where(s => !catalog.Contains(s)).ToList();
            if (unknown.Count > 0)
            {
                throw new TemplateException($"Role '{name}': unknown scopes {string.Join(", ", unknown)}.", element);
            }

            var database = db;
            var tenant = tenantId;
            var wanted = name;
            var ct = cancellationToken;
            var role = await database.Roles.FirstOrDefaultAsync(r => r.TenantId == tenant && r.Name == wanted, ct);
            if (role is { IsBuiltIn: true })
            {
                context.Warn($"Role '{name}' is built in and was not changed.", element);
                continue;
            }

            var existing = new HashSet<Guid>();
            if (role is null)
            {
                role = new Role { Id = Ids.New(), TenantId = tenantId, Name = name, Description = element.Attr("Description"), Scopes = ScopeList.Format(scopes) };
                context.Created(TemplateKinds.Role, name);
                if (!context.DryRun)
                {
                    db.Roles.Add(role);
                }
            }
            else
            {
                var current = ScopeList.Parse(role.Scopes);
                var missing = scopes.Where(s => !current.Contains(s)).ToList();
                if (missing.Count > 0 || role.Description != element.Attr("Description"))
                {
                    context.Updated(TemplateKinds.Role, name, missing.Count > 0 ? "scopes added: " + string.Join(", ", missing) : "description");
                    role.Scopes = ScopeList.Format([.. current, .. missing]);
                    role.Description = element.Attr("Description");
                }

                var roleId = role.Id;
                existing = [.. await database.RoleAssignments.AsNoTracking().Where(a => a.TenantId == tenant && a.RoleId == roleId).Select(a => a.PrincipalId).ToListAsync(ct)];
            }

            foreach (var assignment in element.Elements(TemplateXml.Name("Assignment")))
            {
                var (type, principal, label) = await IdentityTemplateLookups.PrincipalAsync(db, tenantId, assignment, context, cancellationToken);
                if (principal is null)
                {
                    context.Warn($"{label} does not exist; not assigned to role '{name}'.", assignment);
                }
                else if (existing.Add(principal.Value))
                {
                    context.Updated(TemplateKinds.Role, name, $"assigned to {label}");
                    if (!context.DryRun)
                    {
                        db.RoleAssignments.Add(new RoleAssignment { Id = Ids.New(), TenantId = tenantId, RoleId = role.Id, PrincipalId = principal.Value, PrincipalType = type });
                    }
                }
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>Name lookups of the Identity sections (templates use names, never ids). Queries name the tenant.</summary>
internal static class IdentityTemplateLookups
{
    public static Task<Group?> GroupAsync(IdentityDbContext database, Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var wanted = name;
        var ct = cancellationToken;
        return db.Groups.FirstOrDefaultAsync(g => g.TenantId == tenant && g.Name == wanted, ct);
    }

    public static async Task<HashSet<Guid>> MemberIdsAsync(IdentityDbContext database, Guid tenantId, Guid groupId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var group = groupId;
        var ct = cancellationToken;
        return [.. await db.GroupMembers.AsNoTracking().Where(m => m.TenantId == tenant && m.GroupId == group).Select(m => m.UserId).ToListAsync(ct)];
    }

    public static Task<Guid?> UserAsync(IdentityDbContext database, Guid tenantId, string userName, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var normalized = userName.Trim().ToUpperInvariant();
        var ct = cancellationToken;
        return db.Users.AsNoTracking().Where(u => u.TenantId == tenant && u.NormalizedUserName == normalized && u.DeletedAt == null).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>A <c>User</c> or <c>Group</c> attribute: groups of the same template count even before they exist (dry run).</summary>
    public static async Task<(string Type, Guid? Id, string Label)> PrincipalAsync(IdentityDbContext db, Guid tenantId, XElement element, TemplateContext context, CancellationToken ct)
    {
        if (element.Attr("Group") is { } group)
        {
            var id = context.Resolve(TemplateKinds.Group, group) ?? (await GroupAsync(db, tenantId, group, ct))?.Id;
            return (PrincipalTypes.Group, id, $"Group '{group}'");
        }

        if (element.Attr("User") is { } user)
        {
            return (PrincipalTypes.User, await UserAsync(db, tenantId, user, ct), $"User '{user}'");
        }

        throw new TemplateException($"{element.Name.LocalName}: a User or Group attribute is required.", element);
    }
}
