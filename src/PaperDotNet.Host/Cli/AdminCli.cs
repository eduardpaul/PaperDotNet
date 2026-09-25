using System.CommandLine;
using PaperDotNet.Abstractions;
using PaperDotNet.Host.Backup;
using PaperDotNet.Host.Bootstrap;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Provisioning.Features;
using PaperDotNet.Search.Features;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Host.Cli;

/// <summary>Admin commands: <c>paperdotnet migrate | bootstrap | tenant … | user … | backup | restore | reindex | export | import | import-papermerge</c>.</summary>
internal static class AdminCli
{
    private static readonly string[] Commands = ["migrate", "bootstrap", "tenant", "user", "backup", "restore", "reindex", "export", "import", "import-papermerge", "--help", "-h", "-?"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    public static Task<int> RunAsync(IServiceProvider services, string[] args) =>
        Build(services).Parse(args).InvokeAsync();

    private static RootCommand Build(IServiceProvider services)
    {
        var root = new RootCommand("PaperDotNet server. Run without arguments to start the API.");

        var migrate = new Command("migrate", "Apply database migrations of all modules.");
        migrate.SetAction(async (_, ct) =>
        {
            await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(ct);
            Console.WriteLine("Database is up to date.");
            return 0;
        });
        root.Subcommands.Add(migrate);

        var bootstrap = new Command("bootstrap", "Migrate, then create the default tenant and administrator from configuration.");
        bootstrap.SetAction(async (_, ct) =>
        {
            await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(ct);
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TenantBootstrapper>().RunAsync(ct);
            Console.WriteLine("Bootstrap complete.");
            return 0;
        });
        root.Subcommands.Add(bootstrap);

        root.Subcommands.Add(BuildTenantCommand(services));
        root.Subcommands.Add(BuildUserCommand(services));
        root.Subcommands.Add(BuildBackupCommand(services));
        root.Subcommands.Add(BuildRestoreCommand(services));
        root.Subcommands.Add(BuildReindexCommand(services));
        root.Subcommands.Add(BuildExportCommand(services));
        root.Subcommands.Add(BuildImportCommand(services));
        root.Subcommands.Add(BuildPapermergeCommand(services));
        return root;
    }

    /// <summary>A scope in the tenant, acting as the named user (exports and imports see and create what that user may).</summary>
    private static async Task<(AsyncServiceScope? Scope, string? Error)> UserScopeAsync(IServiceProvider services, string tenantIdentifier, string userName, CancellationToken ct)
    {
        TenantSummary? tenant;
        await using (var lookup = services.CreateAsyncScope())
        {
            tenant = await lookup.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync(tenantIdentifier, ct);
        }

        if (tenant is null)
        {
            return (null, $"Tenant '{tenantIdentifier}' does not exist.");
        }

        Guid? userId;
        await using (var tenantScope = services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier))
        {
            userId = await tenantScope.ServiceProvider.GetRequiredService<IUserDirectory>().FindUserAsync(userName, ct);
        }

        return userId is null
            ? (null, $"User '{userName}' does not exist in tenant '{tenantIdentifier}'.")
            : (services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier, userId), null);
    }

    private static Command BuildExportCommand(IServiceProvider services)
    {
        var tenantOption = new Option<string>("--tenant") { Description = "The tenant (identifier).", Required = true };
        var userOption = new Option<string>("--user") { Description = "Export as this user (an administrator for a whole tenant).", Required = true };
        var workspaceOption = new Option<string>("--workspace") { Description = "Only this workspace (name); default: the whole tenant." };
        var output = new Option<string>("--output", "-o") { Description = "Package to write (default: <tenant>-export-<UTC time>.zip)." };
        var export = new Command("export", "Export a tenant or workspace with its content as a package (PLT-13).") { tenantOption, userOption, workspaceOption, output };
        export.SetAction(async (parse, ct) =>
        {
            var tenant = parse.GetRequiredValue(tenantOption);
            var (scope, error) = await UserScopeAsync(services, tenant, parse.GetRequiredValue(userOption), ct);
            if (scope is not { } userScope)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            await using (userScope)
            {
                Guid? workspaceId = null;
                if (parse.GetValue(workspaceOption) is { } name)
                {
                    workspaceId = await userScope.ServiceProvider.GetRequiredService<PaperDotNet.Workspaces.Contracts.IWorkspaceAccess>().FindSharedAsync(name, ct);
                    if (workspaceId is null)
                    {
                        Console.Error.WriteLine($"Workspace '{name}' does not exist.");
                        return 1;
                    }
                }

                var path = parse.GetValue(output) ?? $"{tenant}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
                await using (var file = File.Create(path))
                {
                    await userScope.ServiceProvider.GetRequiredService<PortabilityService>().ExportAsync(workspaceId, file, ct);
                }

                Console.WriteLine($"Package written to {path} ({new FileInfo(path).Length} bytes).");
                return 0;
            }
        });
        return export;
    }

    private static Command BuildImportCommand(IServiceProvider services)
    {
        var file = new Argument<string>("file") { Description = "A package from 'paperdotnet export' or an export download." };
        var tenantOption = new Option<string>("--tenant") { Description = "The tenant (identifier).", Required = true };
        var userOption = new Option<string>("--user") { Description = "Import as this user (an administrator).", Required = true };
        var dryRun = new Option<bool>("--dry-run") { Description = "List the changes without making them." };
        var import = new Command("import", "Import a package into a tenant: creates what is missing, changes nothing that exists (PLT-13).") { file, tenantOption, userOption, dryRun };
        import.SetAction(async (parse, ct) =>
        {
            var (scope, error) = await UserScopeAsync(services, parse.GetRequiredValue(tenantOption), parse.GetRequiredValue(userOption), ct);
            if (scope is not { } userScope)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            await using (userScope)
            {
                await using var package = File.OpenRead(parse.GetRequiredValue(file));
                var (result, errors) = await userScope.ServiceProvider.GetRequiredService<PortabilityService>()
                    .ImportAsync(package, null, parse.GetValue(dryRun), new Dictionary<string, string>(), ct);
                if (result is null)
                {
                    foreach (var problem in errors)
                    {
                        Console.Error.WriteLine(problem);
                    }

                    return 1;
                }

                foreach (var change in result.Changes)
                {
                    Console.WriteLine($"{change.Action} {change.Kind} {change.Name}{(change.Detail is null ? string.Empty : $" ({change.Detail})")}");
                }

                foreach (var warning in result.Warnings)
                {
                    Console.WriteLine($"warning: {warning}");
                }

                Console.WriteLine(result.DryRun ? $"Dry run: {result.Changes.Count} changes planned." : $"Imported: {result.Changes.Count} changes.");
                return 0;
            }
        });
        return import;
    }

    /// <summary>
    /// PLT-15 (ADR-0029): converts a Papermerge 3.6 database and media folder into a package, creates its users (without
    /// passwords) and imports it; with <c>--output</c> it only writes the package.
    /// </summary>
    private static Command BuildPapermergeCommand(IServiceProvider services)
    {
        var dbOption = new Option<string>("--db") { Description = "Connection string of the Papermerge PostgreSQL database.", Required = true };
        var mediaOption = new Option<string>("--media") { Description = "Papermerge's media_root folder (with docvers/).", Required = true };
        var tenantOption = new Option<string?>("--tenant") { Description = "The tenant to import into (identifier)." };
        var userOption = new Option<string?>("--user") { Description = "Import as this user (an administrator)." };
        var workspaceOption = new Option<string>("--workspace") { Description = "Name of the workspace to create.", DefaultValueFactory = _ => "Papermerge" };
        var libraryOption = new Option<string>("--library") { Description = "Name of the library to create.", DefaultValueFactory = _ => "Documents" };
        var output = new Option<string?>("--output", "-o") { Description = "Only write the package to this file (import it later with 'paperdotnet import')." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Convert and list the changes without making them." };
        var unsupported = new Option<bool>("--allow-unsupported-version") { Description = "Read a Papermerge schema version that was not tested." };
        var command = new Command("import-papermerge", "Import a Papermerge 3.6 archive: folders, documents with all versions and OCR text, tags, types, users and sharing (PLT-15).")
        {
            dbOption, mediaOption, tenantOption, userOption, workspaceOption, libraryOption, output, dryRun, unsupported,
        };
        command.SetAction(async (parse, ct) =>
        {
            var options = new PaperDotNet.Import.Papermerge.PapermergeImportOptions(parse.GetRequiredValue(dbOption), parse.GetRequiredValue(mediaOption))
            {
                WorkspaceName = parse.GetRequiredValue(workspaceOption),
                LibraryName = parse.GetRequiredValue(libraryOption),
                AllowUnsupportedVersion = parse.GetValue(unsupported),
            };
            var target = parse.GetValue(output);
            if (target is null && (parse.GetValue(tenantOption) is null || parse.GetValue(userOption) is null))
            {
                Console.Error.WriteLine("Give --tenant and --user to import, or --output to only write the package.");
                return 1;
            }

            var path = target ?? Path.Combine(Path.GetTempPath(), $"papermerge-{Guid.CreateVersion7():N}.zip");
            try
            {
                PaperDotNet.Import.Papermerge.PapermergeConversion conversion;
                try
                {
                    await using var file = File.Create(path);
                    conversion = await PaperDotNet.Import.Papermerge.PapermergeConverter.ConvertAsync(options, file, ct);
                }
                catch (PaperDotNet.Import.Papermerge.PapermergeImportException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }

                Console.WriteLine($"Papermerge schema {conversion.SchemaVersion}: {conversion.Folders} folders, {conversion.Documents} documents, " +
                                  $"{conversion.Versions} file versions, {conversion.Tags} tags, {conversion.Users.Count} users.");
                foreach (var warning in conversion.Warnings)
                {
                    Console.WriteLine($"warning: {warning}");
                }

                if (target is not null)
                {
                    Console.WriteLine($"Package written to {target}. Create the users, then run 'paperdotnet import'.");
                    return 0;
                }

                var (scope, error) = await UserScopeAsync(services, parse.GetValue(tenantOption)!, parse.GetValue(userOption)!, ct);
                if (scope is not { } userScope)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                await using (userScope)
                {
                    var directory = userScope.ServiceProvider.GetRequiredService<IUserDirectory>();
                    foreach (var user in conversion.Users)
                    {
                        if (await directory.FindUserAsync(user.UserName, ct) is not null)
                        {
                            continue;
                        }

                        if (parse.GetValue(dryRun))
                        {
                            Console.WriteLine($"Create user {user.UserName}");
                            continue;
                        }

                        try
                        {
                            await directory.CreateUserAsync(new NewUser(user.UserName, null, user.DisplayName, user.Email), ct);
                            Console.WriteLine($"Created user {user.UserName} (no password yet: reset it to let them sign in).");
                        }
                        catch (UserCreationException ex)
                        {
                            Console.WriteLine($"warning: user {user.UserName} was not created: {string.Join(" ", ex.Errors)}");
                        }
                    }

                    await using var package = File.OpenRead(path);
                    var (result, errors) = await userScope.ServiceProvider.GetRequiredService<PortabilityService>()
                        .ImportAsync(package, null, parse.GetValue(dryRun), new Dictionary<string, string>(), ct);
                    if (result is null)
                    {
                        foreach (var problem in errors)
                        {
                            Console.Error.WriteLine(problem);
                        }

                        return 1;
                    }

                    foreach (var change in result.Changes)
                    {
                        Console.WriteLine($"{change.Action} {change.Kind} {change.Name}{(change.Detail is null ? string.Empty : $" ({change.Detail})")}");
                    }

                    foreach (var warning in result.Warnings)
                    {
                        Console.WriteLine($"warning: {warning}");
                    }

                    Console.WriteLine(result.DryRun ? $"Dry run: {result.Changes.Count} changes planned." : $"Imported: {result.Changes.Count} changes.");
                    return 0;
                }
            }
            finally
            {
                if (target is null)
                {
                    File.Delete(path);
                }
            }
        });
        return command;
    }

    private static Command BuildBackupCommand(IServiceProvider services)
    {
        var output = new Option<string>("--output", "-o")
        {
            Description = "Archive to write (default: paperdotnet-backup-<UTC time>.tar.gz in the current directory).",
        };
        var backup = new Command("backup", "Back up the database and the stored files into one .tar.gz (PLT-12). Safe while the server runs.") { output };
        backup.SetAction(async (parse, ct) =>
        {
            var path = parse.GetValue(output) ?? $"paperdotnet-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.gz";
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<BackupService>().BackupAsync(path, ct);
            Console.WriteLine($"Backup written to {result.Path} ({result.Files} files, {result.Bytes} bytes).");
            return 0;
        });
        return backup;
    }

    private static Command BuildRestoreCommand(IServiceProvider services)
    {
        var file = new Argument<string>("file") { Description = "A backup archive from 'paperdotnet backup'." };
        var force = new Option<bool>("--force") { Description = "Replace an installation that already has data." };
        var restore = new Command("restore", "Restore a backup (stop the server first), then migrate.") { file, force };
        restore.SetAction(async (parse, ct) =>
        {
            await using var scope = services.CreateAsyncScope();
            try
            {
                var manifest = await scope.ServiceProvider.GetRequiredService<BackupService>().RestoreAsync(parse.GetRequiredValue(file), parse.GetValue(force), ct);
                Console.WriteLine($"Restored the backup of {manifest.CreatedAt:u} ({manifest.Files} files).");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });
        return restore;
    }

    private static Command BuildReindexCommand(IServiceProvider services)
    {
        var tenantOption = new Option<string>("--tenant") { Description = "Only this tenant (default: all active tenants)." };
        var reindex = new Command("reindex", "Rebuild the search index (SRC-10).") { tenantOption };
        reindex.SetAction(async (parse, ct) =>
        {
            await using var lookup = services.CreateAsyncScope();
            var tenants = (await lookup.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(ct))
                .Where(t => parse.GetValue(tenantOption) is { } only ? t.Identifier == only : t.Status == TenantStatus.Active)
                .ToList();
            if (tenants.Count == 0)
            {
                Console.Error.WriteLine("No matching tenant.");
                return 1;
            }

            foreach (var tenant in tenants)
            {
                await using var scope = services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
                var sources = await scope.ServiceProvider.GetRequiredService<SearchReindexer>().ReindexAsync(_ => Task.CompletedTask, ct);
                Console.WriteLine($"{tenant.Identifier}: reindexed {string.Join(", ", sources)}.");
            }

            return 0;
        });
        return reindex;
    }

    private static Command BuildTenantCommand(IServiceProvider services)
    {
        var tenant = new Command("tenant", "Manage tenants.");

        var list = new Command("list", "List tenants.");
        list.SetAction(async (_, ct) =>
        {
            await using var scope = services.CreateAsyncScope();
            foreach (var t in await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(ct))
            {
                Console.WriteLine($"{t.Identifier,-24} {t.Status,-10} {t.Name} {string.Join(',', t.Hosts)}");
            }

            return 0;
        });
        tenant.Subcommands.Add(list);

        var identifier = new Option<string>("--identifier") { Description = "URL-safe tenant identifier.", Required = true };
        var name = new Option<string>("--name") { Description = "Display name." };
        var hosts = new Option<string[]>("--host") { Description = "Custom host name mapped to the tenant (repeatable).", AllowMultipleArgumentsPerToken = true };
        var create = new Command("create", "Create a tenant.") { identifier, name, hosts };
        create.SetAction(async (parse, ct) =>
        {
            await using var scope = services.CreateAsyncScope();
            var created = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().CreateAsync(
                parse.GetRequiredValue(identifier), parse.GetValue(name) ?? string.Empty, parse.GetValue(hosts) ?? [], ct);
            Console.WriteLine($"Created tenant '{created.Identifier}' ({created.Id}).");
            return 0;
        });
        tenant.Subcommands.Add(create);

        foreach (var (command, status) in new[] { ("suspend", TenantStatus.Suspended), ("activate", TenantStatus.Active) })
        {
            var id = new Argument<string>("identifier");
            var change = new Command(command, $"Set a tenant to {status}.") { id };
            change.SetAction(async (parse, ct) =>
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().SetStatusAsync(parse.GetRequiredValue(id), status, ct);
                Console.WriteLine($"Tenant is now {status}.");
                return 0;
            });
            tenant.Subcommands.Add(change);
        }

        return tenant;
    }

    private static Command BuildUserCommand(IServiceProvider services)
    {
        var tenantOption = new Option<string>("--tenant") { Description = "Tenant identifier.", Required = true };
        var userName = new Option<string>("--username") { Required = true };
        var password = new Option<string>("--password") { Required = true };
        var displayName = new Option<string>("--display-name");
        var email = new Option<string>("--email");
        var admin = new Option<bool>("--admin") { Description = "Also grant the Administrator role." };
        var create = new Command("create", "Create a user.") { tenantOption, userName, password, displayName, email, admin };
        create.SetAction(async (parse, ct) =>
        {
            await using var lookup = services.CreateAsyncScope();
            var tenant = await lookup.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync(parse.GetRequiredValue(tenantOption), ct);
            if (tenant is null)
            {
                Console.Error.WriteLine("Tenant not found.");
                return 1;
            }

            await using var scope = services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
            var id = await scope.ServiceProvider.GetRequiredService<IUserDirectory>().CreateUserAsync(
                new NewUser(parse.GetRequiredValue(userName), parse.GetRequiredValue(password), parse.GetValue(displayName), parse.GetValue(email), parse.GetValue(admin)),
                ct);
            Console.WriteLine($"Created user {id}.");
            return 0;
        });

        return new Command("user", "Manage users.") { create };
    }
}
