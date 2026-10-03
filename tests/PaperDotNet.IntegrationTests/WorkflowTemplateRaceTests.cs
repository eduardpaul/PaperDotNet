using System.Data.Common;
using System.Net.Http.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Provisioning.Contracts;
using PaperDotNet.Workflows.Data;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.IntegrationTests;

public sealed class WorkflowTemplateRaceTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("documents.text", "Read the text")]
    [InlineData("documents.pages", "Render pages")]
    public async Task Templates_reuse_defaults_created_between_identity_and_name_lookups(string key, string name)
    {
        var tenant = await factory.CreateTenantAsync($"template-race-{key.Replace('.', '-')}");
        var client = await ApiClient.CreateAsync(factory, tenant.Identifier);
        var workspace = await client.CreateWorkspaceAsync("Archive");
        var library = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists",
            new { name = "Files", templateKey = "documents" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var scopes = factory.Services.GetRequiredService<ITenantScopeFactory>();
        await using var scope = scopes.CreateScope(tenant.Id, tenant.Identifier);
        var services = scope.ServiceProvider;
        var interceptor = new CreateDefaultsAfterLookup(async () =>
        {
            // The same operation an imported-item event performs, in its own DbContext.
            await using var eventScope = scopes.CreateScope(tenant.Id, tenant.Identifier);
            var eventServices = eventScope.ServiceProvider;
            var list = await eventServices.GetRequiredService<IListItemStore>().AsSystem().GetListAsync(workspace, library, Ct);
            await eventServices.GetRequiredService<BuiltInWorkflows>().EnsureDefaultsAsync(list!, tenant.Id, Ct);
        });
        var options = new DbContextOptionsBuilder<WorkflowsDbContext>(services.GetRequiredService<DbContextOptions<WorkflowsDbContext>>())
            .AddInterceptors(interceptor).Options;
        await using var db = new WorkflowsDbContext(options, services.GetRequiredService<ITenantContext>());
        var builtIns = ActivatorUtilities.CreateInstance<BuiltInWorkflows>(services, db);
        var handler = ActivatorUtilities.CreateInstance<WorkflowTemplateHandler>(services, db, builtIns);
        var context = new TemplateContext(TemplateScope.Workspace, dryRun: false) { WorkspaceId = workspace, WorkspaceName = "Archive" };
        var section = new XElement(WorkflowTemplateHandler.Ns + "Workflows",
            new XElement(WorkflowTemplateHandler.Ns + "Workflow", new XAttribute("Name", $"{name} (Files)"),
                new XAttribute("BuiltIn", key), new XAttribute("List", "Files"), new XAttribute("Enabled", false), "{}"));

        await handler.ApplyAsync(section, context, Ct);
        await context.RunDeferredAsync(Ct);

        Assert.True(interceptor.Created);
        var row = Assert.Single(await db.Workflows.AsNoTracking().Where(w => w.WorkspaceId == workspace && w.BuiltInKey == key).ToListAsync(Ct));
        Assert.Equal(library, row.ListId);
        Assert.False(row.Enabled); // The template overrides the concurrently created default.
        Assert.Equal(1, row.CurrentVersion);
        Assert.Equal(1, await db.Versions.CountAsync(v => v.WorkflowId == row.Id, Ct));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("documents.pages", false)]
    [InlineData("documents.text", true)]
    public async Task Templates_reject_same_name_workflows_with_a_different_identity(string? builtInKey, bool differentLibrary)
    {
        var tenant = await factory.CreateTenantAsync($"template-collision-{builtInKey?.Replace('.', '-') ?? "custom"}-{differentLibrary}".ToLowerInvariant());
        var client = await ApiClient.CreateAsync(factory, tenant.Identifier);
        var workspace = await client.CreateWorkspaceAsync("Archive");
        var library = (await (await client.PostAsJsonAsync($"/v1.0/workspaces/{workspace}/lists",
            new { name = "Files", templateKey = "documents" }, Ct)).ReadJsonAsync()).GetProperty("id").GetGuid();
        await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
        var db = scope.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        db.Workflows.Add(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace,
            Name = "Read the text (Files)",
            Trigger = "manual",
            BuiltInKey = builtInKey,
            ListId = differentLibrary ? Guid.NewGuid() : library,
        });
        await db.SaveChangesAsync(Ct);
        var context = new TemplateContext(TemplateScope.Workspace, dryRun: false) { WorkspaceId = workspace, WorkspaceName = "Archive" };
        var section = new XElement(WorkflowTemplateHandler.Ns + "Workflows",
            new XElement(WorkflowTemplateHandler.Ns + "Workflow", new XAttribute("Name", "Read the text (Files)"),
                new XAttribute("BuiltIn", "documents.text"), new XAttribute("List", "Files"), "{}"));
        await ActivatorUtilities.CreateInstance<WorkflowTemplateHandler>(scope.ServiceProvider).ApplyAsync(section, context, Ct);

        var error = await Assert.ThrowsAsync<TemplateException>(() => context.RunDeferredAsync(Ct));
        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.Single(await db.Workflows.Where(w => w.WorkspaceId == workspace).ToListAsync(Ct));
    }

    private sealed class CreateDefaultsAfterLookup(Func<Task> create) : DbCommandInterceptor
    {
        public bool Created { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Created && command.CommandText.Contains("built_in_key", StringComparison.Ordinal)
                && command.CommandText.Contains("LIMIT 1", StringComparison.Ordinal))
            {
                Assert.False(await result.ReadAsync(cancellationToken));
                Created = true;
                await create();
            }

            return result;
        }
    }
}
