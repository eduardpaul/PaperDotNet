using System.CommandLine;
using PaperDotNet.Abstractions;
using PaperDotNet.Host.Backup;
using PaperDotNet.Host.Bootstrap;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Persistence;
using PaperDotNet.Search.Features;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.Host.Cli;

/// <summary>Admin commands: <c>paperdotnet migrate | bootstrap | tenant … | user … | backup | restore | reindex</c>.</summary>
internal static class AdminCli
{
    private static readonly string[] Commands = ["migrate", "bootstrap", "tenant", "user", "backup", "restore", "reindex", "--help", "-h", "-?"];

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
        return root;
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
