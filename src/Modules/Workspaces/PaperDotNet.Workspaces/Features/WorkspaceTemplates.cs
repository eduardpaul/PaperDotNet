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
internal sealed class WorkspaceTemplateContainer(WorkspacesDbContext db, WorkspaceAccess access, IUserDirectory users, ICurrentUser user) : ITemplateContainer
{
    public TemplateLevel Level => TemplateLevel.Workspace;

    public async Task<IReadOnlyList<Guid>> ListAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        if (context.Scope == TemplateScope.Workspace)
        {
            return [context.WorkspaceId!.Value];
        }

        return await (await access.VisibleAsync(db.Workspaces.AsNoTracking(), cancellationToken))
            .Where(w => w.PersonalOwnerId == null).OrderBy(w => w.Name).Select(w => w.Id).ToListAsync(cancellationToken);
    }

    public async Task<XElement> ExportAsync(Guid id, TemplateContext context, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.AsNoTracking().Include(w => w.Members).FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
            ?? throw new TemplateException("The workspace was not found.");
        if (workspace.PersonalOwnerId is not null)
        {
            throw new TemplateException("Personal workspaces cannot be exported.");
        }

        context.WorkspaceName = workspace.Name;
        var names = await users.GetUserNamesAsync([.. workspace.Members.Select(m => m.UserId)], cancellationToken);
        var members = workspace.Members.Where(m => names.ContainsKey(m.UserId)).OrderBy(m => names[m.UserId], StringComparer.Ordinal)
            .Select(m => new XElement(TemplateXml.Name("Member"), new XAttribute("User", names[m.UserId]), new XAttribute("Role", m.Role.ToString())))
            .ToList();
        return new XElement(TemplateXml.Name("Workspace"), members.Count == 0 ? null : new XElement(TemplateXml.Name("Members"), members))
            .With("Name", workspace.Name).With("Description", workspace.Description);
    }

    public async Task ApplyAsync(XElement element, TemplateContext context, CancellationToken cancellationToken)
    {
        var name = element.RequiredAttr("Name").Trim();
        var description = element.Attr("Description");
        Workspace? workspace;
        if (context.WorkspaceId is { } target)
        {
            workspace = await db.Workspaces.Include(w => w.Members).FirstOrDefaultAsync(w => w.Id == target, cancellationToken);
            if (workspace is null || workspace.PersonalOwnerId is not null)
            {
                throw new TemplateException("Templates cannot be applied to this workspace.", element);
            }
        }
        else
        {
            workspace = await db.Workspaces.Include(w => w.Members).Where(w => w.PersonalOwnerId == null && w.Name == name)
                .OrderBy(w => w.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        }

        var saved = true;
        if (workspace is null)
        {
            workspace = new Workspace { Id = Ids.New(), Name = name, Description = description };
            if (user.UserId is { } creator)
            {
                workspace.Members.Add(new WorkspaceMember { UserId = creator, Role = WorkspaceRole.Owner });
            }

            context.Created(TemplateKinds.Workspace, name);
            if (context.DryRun)
            {
                context.IsPlanned = true;
                saved = false;
            }
            else
            {
                db.Workspaces.Add(workspace);
            }
        }
        else if (workspace.Description != description)
        {
            context.Updated(TemplateKinds.Workspace, workspace.Name, "description");
            workspace.Description = description;
        }

        foreach (var member in element.Element(TemplateXml.Name("Members"))?.Elements(TemplateXml.Name("Member")) ?? [])
        {
            var userName = member.RequiredAttr("User");
            var role = member.EnumAttr("Role", WorkspaceRole.Member);
            if (await users.FindUserAsync(userName, cancellationToken) is not { } userId)
            {
                context.Warn($"User '{userName}' does not exist; not added to workspace '{name}'.", member);
                continue;
            }

            var existing = workspace.Members.FirstOrDefault(m => m.UserId == userId);
            if (existing is null)
            {
                context.Updated(TemplateKinds.Workspace, name, $"member added: {userName} ({role})");
                workspace.Members.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = userId, Role = role });
            }
            else if (existing.Role != role)
            {
                context.Updated(TemplateKinds.Workspace, name, $"member {userName}: {existing.Role} → {role}");
                existing.Role = role;
            }
        }

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        context.WorkspaceId = workspace.Id;
        context.WorkspaceName = name;
        context.IsPlanned = !saved;
        context.Register(TemplateKinds.Workspace, name, workspace.Id);
    }
}
