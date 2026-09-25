using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperDotNet.Abstractions;
using PaperDotNet.Automation.Data;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Documents.Data;
using PaperDotNet.ExtensionHost.Data;
using PaperDotNet.Identity.Data;
using PaperDotNet.Jobs.Data;
using PaperDotNet.Lists.Data;
using PaperDotNet.Notifications.Data;
using PaperDotNet.Persistence.PostgreSql;
using PaperDotNet.Search.Data;
using PaperDotNet.Tasks.Data;
using PaperDotNet.Taxonomy.Data;
using PaperDotNet.Tenancy.Data;
using PaperDotNet.Workspaces.Data;

namespace PaperDotNet.Migrations.PostgreSql;

/// <summary>Creates module DbContexts for <c>dotnet ef</c>. No tenant: design time only builds the model.</summary>
internal static class DesignTime
{
    public static DbContextOptions<T> Options<T>(string schema)
        where T : DbContext
    {
        var connection = Environment.GetEnvironmentVariable("PAPERDOTNET_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=paperdotnet_design;Username=postgres;Password=postgres";
        var builder = new DbContextOptionsBuilder<T>();
        PostgreSqlServiceCollectionExtensions.ConfigureForDesignTime(builder, connection, schema);
        return builder.Options;
    }

    /// <summary>Options for the Identity context: Identity reads its schema version (3: passkeys) from the app services.</summary>
    public static DbContextOptions<IdentityDbContext> IdentityOptions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(
            services, o => o.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3);
        var builder = new DbContextOptionsBuilder<IdentityDbContext>(Options<IdentityDbContext>(IdentityDbContext.Schema));
        builder.UseApplicationServiceProvider(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));
        return builder.Options;
    }

    public static readonly ITenantContext NoTenant = new NoTenantContext();

    private sealed class NoTenantContext : ITenantContext
    {
        public Guid? TenantId => null;

        public string? TenantIdentifier => null;
    }
}

internal sealed class TenancyDesignTimeFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<TenancyDbContext>(TenancyDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(DesignTime.IdentityOptions(), DesignTime.NoTenant);
}

internal sealed class WorkspacesDesignTimeFactory : IDesignTimeDbContextFactory<WorkspacesDbContext>
{
    public WorkspacesDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<WorkspacesDbContext>(WorkspacesDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class ListsDesignTimeFactory : IDesignTimeDbContextFactory<ListsDbContext>
{
    public ListsDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<ListsDbContext>(ListsDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class JobsDesignTimeFactory : IDesignTimeDbContextFactory<JobsDbContext>
{
    public JobsDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<JobsDbContext>(JobsDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class TaxonomyDesignTimeFactory : IDesignTimeDbContextFactory<TaxonomyDbContext>
{
    public TaxonomyDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<TaxonomyDbContext>(TaxonomyDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class SearchDesignTimeFactory : IDesignTimeDbContextFactory<SearchDbContext>
{
    public SearchDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<SearchDbContext>(SearchDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class ExtensionsDesignTimeFactory : IDesignTimeDbContextFactory<ExtensionsDbContext>
{
    public ExtensionsDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<ExtensionsDbContext>(ExtensionsDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class DocumentsDesignTimeFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<DocumentsDbContext>(DocumentsDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class TasksDesignTimeFactory : IDesignTimeDbContextFactory<TasksDbContext>
{
    public TasksDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<TasksDbContext>(TasksDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class CalendarDesignTimeFactory : IDesignTimeDbContextFactory<CalendarDbContext>
{
    public CalendarDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<CalendarDbContext>(CalendarDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<NotificationsDbContext>(NotificationsDbContext.Schema), DesignTime.NoTenant);
}

internal sealed class AutomationDesignTimeFactory : IDesignTimeDbContextFactory<AutomationDbContext>
{
    public AutomationDbContext CreateDbContext(string[] args) =>
        new(DesignTime.Options<AutomationDbContext>(AutomationDbContext.Schema), DesignTime.NoTenant);
}
