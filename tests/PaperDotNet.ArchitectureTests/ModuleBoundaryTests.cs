using System.Reflection;
using System.Text.RegularExpressions;

namespace PaperDotNet.ArchitectureTests;

/// <summary>
/// Architecture rules from docs/technical-approach.md, checked on assembly
/// references and source files.
/// </summary>
public sealed partial class ModuleBoundaryTests
{
    private static readonly string[] Modules = ["Tenancy", "Identity", "Workspaces", "Lists", "Jobs", "Taxonomy", "Audit", "Search", "ExtensionHost"];

    /// <summary>Modules that expose a contracts assembly.</summary>
    private static readonly string[] ContractModules = ["Tenancy", "Identity", "Workspaces", "Lists", "Jobs", "Taxonomy", "Search"];

    private static readonly string[] ProviderAgnostic =
    [
        "PaperDotNet.Abstractions",
        "PaperDotNet.Api",
        "PaperDotNet.Persistence",
        "PaperDotNet.Extensions.Abstractions",
        .. Modules.Select(m => $"PaperDotNet.{m}"),
        .. ContractModules.Select(m => $"PaperDotNet.{m}.Contracts"),
    ];

    public static TheoryData<string> ProviderAgnosticAssemblies => [.. ProviderAgnostic];

    public static TheoryData<string> ModuleAssemblies => [.. Modules];

    [Theory]
    [MemberData(nameof(ProviderAgnosticAssemblies))]
    public void Only_provider_projects_reference_database_providers(string assembly)
    {
        var references = Load(assembly).GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Npgsql", StringComparison.Ordinal)
            || r.StartsWith("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal)
            || r.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ProviderAgnosticAssemblies))]
    public void Only_the_messaging_building_block_references_Wolverine(string assembly)
    {
        var references = Load(assembly).GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Wolverine", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblies))]
    public void Modules_only_reference_other_modules_through_contracts(string module)
    {
        var references = Load($"PaperDotNet.{module}").GetReferencedAssemblies().Select(a => a.Name!).ToHashSet();

        foreach (var other in Modules.Where(m => m != module))
        {
            Assert.DoesNotContain($"PaperDotNet.{other}", references);
        }
    }

    [Fact]
    public void Contracts_do_not_reference_implementations()
    {
        foreach (var module in ContractModules)
        {
            var references = Load($"PaperDotNet.{module}.Contracts").GetReferencedAssemblies().Select(a => a.Name!).ToList();
            Assert.DoesNotContain(references, r => Modules.Any(m => r == $"PaperDotNet.{m}"));
        }
    }

    /// <summary>Extensions see only the SDK: no module implementations, persistence or messaging internals (ADR-0014).</summary>
    [Theory]
    [InlineData("PaperDotNet.Extensions.Abstractions")]
    [InlineData("PaperDotNet.Samples.Invoices")]
    public void The_sdk_and_extensions_only_reference_contracts(string assembly)
    {
        var references = Load(assembly).GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => Modules.Any(m => r == $"PaperDotNet.{m}"));
        Assert.DoesNotContain(references, r => r is "PaperDotNet.Persistence" or "PaperDotNet.Messaging" || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void The_tenant_filter_is_never_disabled_in_source()
    {
        var offenders = SourceFiles()
            .Where(f => DisablesTenantFilter().IsMatch(File.ReadAllText(f)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_raw_sql_outside_the_provider_projects()
    {
        var offenders = SourceFiles()
            .Where(f => !f.Contains("PaperDotNet.Persistence.PostgreSql", StringComparison.Ordinal)
                        && !f.Contains("PaperDotNet.Persistence.Sqlite", StringComparison.Ordinal))
            .Where(f => RawSql().IsMatch(File.ReadAllText(f)))
            .ToList();

        Assert.Empty(offenders);
    }

    private static Assembly Load(string name) => Assembly.Load(name);

    private static IEnumerable<string> SourceFiles()
    {
        var root = FindRepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaperDotNet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary><c>IgnoreQueryFilters()</c> (all filters) or <c>IgnoreQueryFilters([.. QueryFilters.Tenant ..])</c>.</summary>
    [GeneratedRegex(@"IgnoreQueryFilters\(\s*(\)|[^)]*QueryFilters\.Tenant)")]
    private static partial Regex DisablesTenantFilter();

    [GeneratedRegex(@"\b(FromSql|FromSqlRaw|FromSqlInterpolated|ExecuteSql|ExecuteSqlRaw|ExecuteSqlInterpolated|SqlQuery|SqlQueryRaw)\b")]
    private static partial Regex RawSql();
}
