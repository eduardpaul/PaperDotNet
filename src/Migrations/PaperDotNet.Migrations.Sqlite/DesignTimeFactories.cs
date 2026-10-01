using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Audit.Data;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Collaboration.Data;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.Identity.Data;
using PaperDotNet.Jobs.Data;
using PaperDotNet.Lists.Data;
using PaperDotNet.Notes.Data;
using PaperDotNet.Notifications.Data;
using PaperDotNet.Persistence.Sqlite;
using PaperDotNet.Provisioning.Data;
using PaperDotNet.Search.Data;
using PaperDotNet.Tasks.Data;
using PaperDotNet.Taxonomy.Data;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Migrations.Sqlite;

// The EF Core tools create the module DbContexts for migrations through these (eng/schema.sh).

internal sealed class IdentityFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<IdentityDbContext>());
}

internal sealed class ListsFactory : IDesignTimeDbContextFactory<ListsDbContext>
{
    public ListsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<ListsDbContext>());
}

internal sealed class AuditFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<AuditDbContext>());
}

internal sealed class WorkflowsFactory : IDesignTimeDbContextFactory<WorkflowsDbContext>
{
    public WorkflowsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<WorkflowsDbContext>());
}

internal sealed class JobsFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    public JobsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<JobsDbContext>());
}

internal sealed class WorkspacesFactory : IDesignTimeDbContextFactory<WorkspacesDbContext>
{
    public WorkspacesDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<WorkspacesDbContext>());
}

internal sealed class ExtensionsFactory : IDesignTimeDbContextFactory<ExtensionsDbContext>
{
    public ExtensionsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<ExtensionsDbContext>());
}

internal sealed class NotificationsFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<NotificationsDbContext>());
}

internal sealed class CollaborationFactory : IDesignTimeDbContextFactory<CollaborationDbContext>
{
    public CollaborationDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<CollaborationDbContext>());
}

internal sealed class NotesFactory : IDesignTimeDbContextFactory<NotesDbContext>
{
    public NotesDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<NotesDbContext>());
}

internal sealed class TasksFactory : IDesignTimeDbContextFactory<TasksDbContext>
{
    public TasksDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<TasksDbContext>());
}

internal sealed class CalendarFactory : IDesignTimeDbContextFactory<CalendarDbContext>
{
    public CalendarDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<CalendarDbContext>());
}

internal sealed class TaxonomyFactory : IDesignTimeDbContextFactory<TaxonomyDbContext>
{
    public TaxonomyDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<TaxonomyDbContext>());
}

internal sealed class SearchFactory : IDesignTimeDbContextFactory<SearchDbContext>
{
    public SearchDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<SearchDbContext>());
}

internal sealed class ProvisioningFactory : IDesignTimeDbContextFactory<ProvisioningDbContext>
{
    public ProvisioningDbContext CreateDbContext(string[] args) => new(SqliteDesignTime.Options<ProvisioningDbContext>());
}
