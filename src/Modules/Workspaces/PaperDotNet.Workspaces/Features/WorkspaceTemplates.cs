using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Workspaces.Features;

/// <summary>
/// The <c>Workspace</c> element of templates (PRV-01/02): name, description and members by user name.
/// Matched by name among shared workspaces (personal ones are never part of templates); members are
/// added or get the template's role, never removed.
/// </summary>
internal sealed class WorkspaceTemplateContainer(WorkspacesDbContext db, WorkspaceAccess access, IUserDirectory users) : ITemplateContainer
{
    public TemplateLevel Level => TemplateLevel.Workspace;

    public async Task<IReadOnlyList<Guid>> ListAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        if (context.Scope == TemplateScope.Workspace)
        {
            return [context.WorkspaceId!.Value];
        }

        if (context.Actor.UserId is not { } user)
        {
            return [];
        }

        var visible = (await access.GetMembershipsAsync(context.TenantId, user, cancellationToken)).Select(m => m.WorkspaceId).ToHashSet();
        var context2 = db;
        var tenant = context.TenantId;
        var ct = cancellationToken;
        var shared = await context2.Workspaces.AsNoTracking()
            .Where(w => w.TenantId == tenant && w.PersonalOwnerId == null && w.DeletedAt == null)
            .OrderBy(w => w.Name).Select(w => w.Id).ToListAsync(ct);
        return [.. shared.Where(visible.Contains)];
    }

    public async Task<XElement> ExportAsync(Guid id, TemplateContext context, CancellationToken cancellationToken)
    {
        var workspace = await FindAsync(context.TenantId, id, cancellationToken) ?? throw new TemplateException("The workspace was not found.");
        if (workspace.PersonalOwnerId is not null)
        {
            throw new TemplateException("Personal workspaces cannot be exported.");
        }

        context.WorkspaceName = workspace.Name;
        var members = await MembersAsync(context.TenantId, id, cancellationToken);
        var names = await users.GetUserNamesAsync(context.TenantId, [.. members.Select(m => m.UserId)], cancellationToken);
        var elements = members.Where(m => names.ContainsKey(m.UserId)).OrderBy(m => names[m.UserId], StringComparer.Ordinal)
            .Select(m => new XElement(TemplateXml.Name("Member"), new XAttribute("User", names[m.UserId]), new XAttribute("Role", XmlRole(m.Role))))
            .ToList();
        return new XElement(TemplateXml.Name("Workspace"), elements.Count == 0 ? null : new XElement(TemplateXml.Name("Members"), elements))
            .With("Name", workspace.Name).With("Description", workspace.Description);
    }

    public async Task ApplyAsync(XElement element, TemplateContext context, CancellationToken cancellationToken)
    {
        var tenantId = context.TenantId;
        var name = element.RequiredAttr("Name").Trim();
        var description = element.Attr("Description");
        Workspace? workspace;
        if (context.WorkspaceId is { } target)
        {
            workspace = await FindAsync(tenantId, target, cancellationToken, tracking: true);
            if (workspace is null || workspace.PersonalOwnerId is not null)
            {
                throw new TemplateException("Templates cannot be applied to this workspace.", element);
            }
        }
        else
        {
            var context2 = db;
            var tenant = tenantId;
            var wanted = name;
            var ct = cancellationToken;
            workspace = await context2.Workspaces
                .Where(w => w.TenantId == tenant && w.PersonalOwnerId == null && w.DeletedAt == null && w.Name == wanted)
                .OrderBy(w => w.Id).FirstOrDefaultAsync(ct);
        }

        var planned = false;
        var members = new List<WorkspaceMember>();
        if (workspace is null)
        {
            workspace = new Workspace { Id = Ids.New(), TenantId = tenantId, Name = name, Description = description };
            context.Created(TemplateKinds.Workspace, name);
            planned = context.DryRun;
            if (!planned)
            {
                db.Workspaces.Add(workspace);
            }

            // The creator owns the new workspace (in the dry run too, so the plan matches the apply).
            if (context.Actor.UserId is { } creator)
            {
                var owner = new WorkspaceMember { TenantId = tenantId, WorkspaceId = workspace.Id, UserId = creator, Role = WorkspaceRoles.Owner };
                members.Add(owner);
                if (!planned)
                {
                    db.Members.Add(owner);
                }
            }
        }
        else
        {
            members = await MembersAsync(tenantId, workspace.Id, cancellationToken, tracking: true);
            if (workspace.Description != description)
            {
                context.Updated(TemplateKinds.Workspace, workspace.Name, "description");
                workspace.Description = description;
            }
        }

        foreach (var member in element.Element(TemplateXml.Name("Members"))?.Elements(TemplateXml.Name("Member")) ?? [])
        {
            var userName = member.RequiredAttr("User");
            var role = StoredRole(member.Attr("Role") ?? "Member");
            if (await users.FindUserAsync(tenantId, userName, cancellationToken) is not { } userId)
            {
                context.Warn($"User '{userName}' does not exist; not added to workspace '{name}'.", member);
                continue;
            }

            var existing = members.FirstOrDefault(m => m.UserId == userId);
            if (existing is null)
            {
                context.Updated(TemplateKinds.Workspace, name, $"member added: {userName} ({XmlRole(role)})");
                var added = new WorkspaceMember { TenantId = tenantId, WorkspaceId = workspace.Id, UserId = userId, Role = role };
                members.Add(added);
                if (!planned && !context.DryRun)
                {
                    db.Members.Add(added);
                }
            }
            else if (existing.Role != role)
            {
                context.Updated(TemplateKinds.Workspace, name, $"member {userName}: {XmlRole(existing.Role)} → {XmlRole(role)}");
                existing.Role = role;
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        context.WorkspaceId = workspace.Id;
        context.WorkspaceName = name;
        context.IsPlanned = planned;
        context.Register(TemplateKinds.Workspace, name, workspace.Id);
    }

    private Task<Workspace?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken, bool tracking = false)
    {
        var context = db;
        var tenant = tenantId;
        var workspaceId = id;
        var ct = cancellationToken;
        return tracking
            ? context.Workspaces.FirstOrDefaultAsync(w => w.TenantId == tenant && w.Id == workspaceId && w.DeletedAt == null, ct)
            : context.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.TenantId == tenant && w.Id == workspaceId && w.DeletedAt == null, ct);
    }

    private async Task<List<WorkspaceMember>> MembersAsync(Guid tenantId, Guid workspaceId, CancellationToken cancellationToken, bool tracking = false)
    {
        var context = db;
        var tenant = tenantId;
        var id = workspaceId;
        var ct = cancellationToken;
        return tracking
            ? await context.Members.Where(m => m.TenantId == tenant && m.WorkspaceId == id).ToListAsync(ct)
            : await context.Members.AsNoTracking().Where(m => m.TenantId == tenant && m.WorkspaceId == id).ToListAsync(ct);
    }

    /// <summary>Roles as templates write them (<c>Owner</c>, <c>Member</c>, <c>Visitor</c>).</summary>
    private static string XmlRole(string role) => role switch
    {
        WorkspaceRoles.Owner => "Owner",
        WorkspaceRoles.Visitor => "Visitor",
        _ => "Member",
    };

    private static string StoredRole(string role) => role switch
    {
        "Owner" => WorkspaceRoles.Owner,
        "Visitor" => WorkspaceRoles.Visitor,
        _ => WorkspaceRoles.Member,
    };
}
