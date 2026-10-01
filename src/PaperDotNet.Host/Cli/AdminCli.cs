using System.CommandLine;
using PaperDotNet.Abstractions;
using PaperDotNet.Host.Backup;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Identity.Features;
using PaperDotNet.Persistence.Sqlite;
using PaperDotNet.Provisioning.Features;
using PaperDotNet.Search.Features;
using PaperDotNet.Tenancy.Contracts;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Host.Cli;

/// <summary>
/// Admin commands of the server binary: <c>paperdotnet migrate | bootstrap | tenant … | user … | backup | restore |
/// reindex | export | import</c>. They run on the built application without starting the server; every command
/// migrates the database first. Papermerge archives become packages with the separate import tool (PLT-15), imported
/// here.
/// </summary>
internal static class AdminCli
{
    private static readonly string[] Commands = ["migrate", "bootstrap", "tenant", "user", "backup", "restore", "reindex", "export", "import", "--help", "-h", "-?"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        var configuration = new InvocationConfiguration { Output = output ?? Console.Out, Error = error ?? Console.Error };
        return await Build(services).Parse(args).InvokeAsync(configuration);
    }

    private static RootCommand Build(IServiceProvider services)
    {
        var root = new RootCommand("PaperDotNet server. Run without arguments to start the API.");

        var migrate = new Command("migrate", "Apply the database migrations of all modules.");
        migrate.SetAction(async (parse, ct) =>
        {
            await SqliteDatabase.MigrateAsync(services, ct);
            parse.InvocationConfiguration.Output.WriteLine("Database is up to date.");
            return 0;
        });
        root.Subcommands.Add(migrate);

        var bootstrap = new Command("bootstrap", "Migrate, then create the configured tenant and administrator (Bootstrap settings).");
        bootstrap.SetAction(async (parse, ct) =>
        {
            await SqliteDatabase.MigrateAsync(services, ct);
            await Bootstrap.RunAsync(services, ct);
            parse.InvocationConfiguration.Output.WriteLine("Bootstrap complete.");
            return 0;
        });
        root.Subcommands.Add(bootstrap);

        root.Subcommands.Add(TenantCommand(services));
        root.Subcommands.Add(UserCommand(services));
        root.Subcommands.Add(BackupCommand(services));
        root.Subcommands.Add(RestoreCommand(services));
        root.Subcommands.Add(ReindexCommand(services));
        root.Subcommands.Add(ExportCommand(services));
        root.Subcommands.Add(ImportCommand(services));
        return root;
    }

    /// <summary>Migrates, then runs <paramref name="action"/> in a scope.</summary>
    private static async Task<int> InScopeAsync(IServiceProvider services, Func<IServiceProvider, Task<int>> action, CancellationToken ct)
    {
        await SqliteDatabase.MigrateAsync(services, ct);
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>The named user of the tenant as the author of changes (exports and imports see and create what that user may).</summary>
    private static async Task<(ChangeActor? Actor, string? Error)> ActorAsync(IServiceProvider services, string tenantIdentifier, string userName, CancellationToken ct)
    {
        if (await services.GetRequiredService<ITenantDirectory>().FindAsync(tenantIdentifier, ct) is not { } tenant)
        {
            return (null, $"Tenant '{tenantIdentifier}' does not exist.");
        }

        return await services.GetRequiredService<IUserDirectory>().FindUserAsync(tenant.Id, userName, ct) is { } userId
            ? (new ChangeActor(tenant.Id, userId), null)
            : (null, $"User '{userName}' does not exist in tenant '{tenantIdentifier}'.");
    }

    private static Command ExportCommand(IServiceProvider services)
    {
        var tenantOption = new Option<string>("--tenant") { Description = "The tenant (identifier).", Required = true };
        var userOption = new Option<string>("--user") { Description = "Export as this user (an administrator for a whole tenant).", Required = true };
        var workspaceOption = new Option<string>("--workspace") { Description = "Only this workspace (name); default: the whole tenant." };
        var outputOption = new Option<string>("--output", "-o") { Description = "Package to write (default: <tenant>-export-<UTC time>.zip)." };
        var export = new Command("export", "Export a tenant or workspace with its content as a package (PLT-13).") { tenantOption, userOption, workspaceOption, outputOption };
        export.SetAction((parse, ct) => InScopeAsync(services, async scope =>
        {
            var tenant = parse.GetRequiredValue(tenantOption);
            var (actor, problem) = await ActorAsync(scope, tenant, parse.GetRequiredValue(userOption), ct);
            if (actor is null)
            {
                await parse.InvocationConfiguration.Error.WriteLineAsync(problem);
                return 1;
            }

            Guid? workspaceId = null;
            if (parse.GetValue(workspaceOption) is { } name)
            {
                workspaceId = await scope.GetRequiredService<IWorkspaceAccess>().FindSharedAsync(actor.TenantId, name, ct);
                if (workspaceId is null)
                {
                    await parse.InvocationConfiguration.Error.WriteLineAsync($"Workspace '{name}' does not exist.");
                    return 1;
                }
            }

            var path = parse.GetValue(outputOption) ?? $"{tenant}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            await using (var file = File.Create(path))
            {
                await scope.GetRequiredService<PortabilityService>().ExportAsync(actor, workspaceId, file, ct);
            }

            await parse.InvocationConfiguration.Output.WriteLineAsync($"Package written to {path} ({new FileInfo(path).Length} bytes).");
            return 0;
        }, ct));
        return export;
    }

    private static Command ImportCommand(IServiceProvider services)
    {
        var file = new Argument<string>("file") { Description = "A package from 'paperdotnet export', an export download or the Papermerge import tool." };
        var tenantOption = new Option<string>("--tenant") { Description = "The tenant (identifier).", Required = true };
        var userOption = new Option<string>("--user") { Description = "Import as this user (an administrator).", Required = true };
        var dryRun = new Option<bool>("--dry-run") { Description = "List the changes without making them." };
        var import = new Command("import", "Import a package into a tenant: creates what is missing, changes nothing that exists (PLT-13).")
        {
            file, tenantOption, userOption, dryRun,
        };
        import.SetAction((parse, ct) => InScopeAsync(services, async scope =>
        {
            var output = parse.InvocationConfiguration.Output;
            var (actor, problem) = await ActorAsync(scope, parse.GetRequiredValue(tenantOption), parse.GetRequiredValue(userOption), ct);
            if (actor is null)
            {
                await parse.InvocationConfiguration.Error.WriteLineAsync(problem);
                return 1;
            }

            await using var package = File.OpenRead(parse.GetRequiredValue(file));

            var (result, errors) = await scope.GetRequiredService<PortabilityService>()
                .ImportAsync(actor, package, null, parse.GetValue(dryRun), new Dictionary<string, string>(), ct);
            if (result is null)
            {
                foreach (var error in errors)
                {
                    await parse.InvocationConfiguration.Error.WriteLineAsync(error);
                }

                return 1;
            }

            foreach (var change in result.Changes)
            {
                await output.WriteLineAsync($"{change.Action} {change.Kind} {change.Name}{(change.Detail is null ? string.Empty : $" ({change.Detail})")}");
            }

            foreach (var warning in result.Warnings)
            {
                await output.WriteLineAsync($"warning: {warning}");
            }

            await output.WriteLineAsync(result.DryRun ? $"Dry run: {result.Changes.Count} changes planned." : $"Imported: {result.Changes.Count} changes.");
            return 0;
        }, ct));
        return import;
    }

    private static Command BackupCommand(IServiceProvider services)
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Archive to write (default: paperdotnet-backup-<UTC time>.tar.gz in the current directory).",
        };
        var backup = new Command("backup", "Back up the database and the stored files into one .tar.gz (PLT-12). Safe while the server runs.") { outputOption };
        backup.SetAction((parse, ct) => InScopeAsync(services, async scope =>
        {
            var path = parse.GetValue(outputOption) ?? $"paperdotnet-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.gz";
            var result = await scope.GetRequiredService<BackupService>().BackupAsync(path, ct);
            await parse.InvocationConfiguration.Output.WriteLineAsync($"Backup written to {result.Path} ({result.Files} files, {result.Bytes} bytes).");
            return 0;
        }, ct));
        return backup;
    }

    private static Command RestoreCommand(IServiceProvider services)
    {
        var file = new Argument<string>("file") { Description = "A backup archive from 'paperdotnet backup'." };
        var force = new Option<bool>("--force") { Description = "Replace an installation that already has data." };
        var restore = new Command("restore", "Restore a backup (stop the server first), then migrate.") { file, force };
        restore.SetAction((parse, ct) => InScopeAsync(services, async scope =>
        {
            try
            {
                var manifest = await scope.GetRequiredService<BackupService>().RestoreAsync(parse.GetRequiredValue(file), parse.GetValue(force), ct);
                await parse.InvocationConfiguration.Output.WriteLineAsync($"Restored the backup of {manifest.CreatedAt:u} ({manifest.Files} files).");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                await parse.InvocationConfiguration.Error.WriteLineAsync(ex.Message);
                return 1;
            }
        }, ct));
        return restore;
    }

    private static Command ReindexCommand(IServiceProvider services)
    {
        var tenantOption = new Option<string>("--tenant") { Description = "Only this tenant (default: all active tenants)." };
        var reindex = new Command("reindex", "Rebuild the search index (SRC-10).") { tenantOption };
        reindex.SetAction((parse, ct) => InScopeAsync(services, async scope =>
        {
            var only = parse.GetValue(tenantOption);
            var tenants = (await scope.GetRequiredService<ITenantDirectory>().ListAsync(ct))
                .Where(t => only is not null ? t.Identifier == only : t.Status == TenantStatus.Active)
                .ToList();
            if (tenants.Count == 0)
            {
                await parse.InvocationConfiguration.Error.WriteLineAsync("No matching tenant.");
                return 1;
            }

            foreach (var tenant in tenants)
            {
                var sources = await scope.GetRequiredService<SearchReindexer>().ReindexAsync(tenant.Id, _ => Task.CompletedTask, ct);
                await parse.InvocationConfiguration.Output.WriteLineAsync($"{tenant.Identifier}: reindexed {string.Join(", ", sources)}.");
            }

            return 0;
        }, ct));
        return reindex;
    }

    private static Command TenantCommand(IServiceProvider services)
    {
        var tenant = new Command("tenant", "Manage tenants.");

        var list = new Command("list", "List tenants.");
        list.SetAction((parse, ct) => InScopeAsync(services, async scope =>
        {
            foreach (var t in await scope.GetRequiredService<ITenantDirectory>().ListAsync(ct))
            {
                await parse.InvocationConfiguration.Output.WriteLineAsync($"{t.Identifier,-24} {t.Status,-10} {t.Name}");
            }

            return 0;
        }, ct));
        tenant.Subcommands.Add(list);

        var identifier = new Option<string>("--identifier") { Description = "URL-safe tenant identifier.", Required = true };
        var name = new Option<string>("--name") { Description = "Display name." };
        var adminName = new Option<string>("--admin") { Description = "User name of the first administrator.", DefaultValueFactory = _ => "admin" };
        var adminPassword = new Option<string>("--admin-password") { Description = "Password of the first administrator.", Required = true };
        var create = new Command("create", "Create a tenant with its first administrator.") { identifier, name, adminName, adminPassword };
        create.SetAction((parse, ct) => InScopeAsync(services, async scope =>
        {
            var id = parse.GetRequiredValue(identifier);
            var (created, _) = await scope.GetRequiredService<TenantProvisioner>().CreateAsync(
                id, parse.GetValue(name) ?? id, parse.GetRequiredValue(adminName), parse.GetRequiredValue(adminPassword), ct);
            await parse.InvocationConfiguration.Output.WriteLineAsync($"Created tenant '{created.Identifier}' ({created.Id}).");
            return 0;
        }, ct));
        tenant.Subcommands.Add(create);

        foreach (var (command, status) in new[] { ("suspend", TenantStatus.Suspended), ("activate", TenantStatus.Active) })
        {
            var id = new Argument<string>("identifier");
            var change = new Command(command, $"Set a tenant to {status}.") { id };
            change.SetAction((parse, ct) => InScopeAsync(services, async scope =>
            {
                await scope.GetRequiredService<ITenantDirectory>().SetStatusAsync(parse.GetRequiredValue(id), status, ct);
                await parse.InvocationConfiguration.Output.WriteLineAsync($"Tenant is now {status}.");
                return 0;
            }, ct));
            tenant.Subcommands.Add(change);
        }

        return tenant;
    }

    private static Command UserCommand(IServiceProvider services)
    {
        var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier.", Required = true };
        var userName = new Option<string>("--username") { Required = true };
        var password = new Option<string>("--password") { Description = "Without one, the user signs in after a password reset (imported users)." };
        var displayName = new Option<string>("--display-name");
        var email = new Option<string>("--email");
        var admin = new Option<bool>("--admin") { Description = "Also grant the Administrator role." };
        var create = new Command("create", "Create a user.") { tenantOption, userName, password, displayName, email, admin };
        create.SetAction((parse, ct) => InScopeAsync(services, async scope =>
        {
            if (await scope.GetRequiredService<ITenantDirectory>().FindAsync(parse.GetRequiredValue(tenantOption), ct) is not { } tenant)
            {
                await parse.InvocationConfiguration.Error.WriteLineAsync("Tenant not found.");
                return 1;
            }

            var id = await scope.GetRequiredService<IUserDirectory>().CreateUserAsync(tenant.Id,
                new NewUser(parse.GetRequiredValue(userName), parse.GetValue(password), parse.GetValue(displayName), parse.GetValue(email), parse.GetValue(admin)),
                ct);
            await parse.InvocationConfiguration.Output.WriteLineAsync($"Created user {id}.");
            return 0;
        }, ct));

        return new Command("user", "Manage users.") { create };
    }
}
