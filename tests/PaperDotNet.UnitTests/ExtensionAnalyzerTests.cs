using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using PaperDotNet.Extensions.Analyzers;

namespace PaperDotNet.UnitTests;

/// <summary>The extension analyzers (EXT-05) on small extension sources.</summary>
public sealed class ExtensionAnalyzerTests
{
    private const string Usings = """
        using System;
        using System.Linq;
        using Microsoft.EntityFrameworkCore;
        using PaperDotNet.Abstractions;
        using PaperDotNet.Extensions;
        using PaperDotNet.Persistence;
        """;

    private static readonly MetadataReference[] References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(PaperDotNet.Extensions.IExtension).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(PaperDotNet.Abstractions.ITenantOwned).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(PaperDotNet.Persistence.QueryFilters).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Microsoft.EntityFrameworkCore.RelationalQueryableExtensions).Assembly.Location),
    ];

    private static async Task<List<string>> AnalyzeAsync(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Acme.Extension",
            [CSharpSyntaxTree.ParseText(Usings + source, cancellationToken: TestContext.Current.CancellationToken)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ExtensionAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal).ToList();
    }

    private const string Registered = """
        [assembly: PaperDotNetExtension(typeof(Acme.AcmeExtension))]
        namespace Acme;
        public sealed class AcmeExtension : IExtension { public void Configure(IExtensionBuilder builder) { } }
        """;

    [Fact]
    public async Task Clean_extension_code_has_no_diagnostics()
    {
        var diagnostics = await AnalyzeAsync(Registered + """
            public sealed class Note : ITenantOwned { public Guid Id { get; set; } public Guid TenantId { get; set; } public DateTimeOffset? DeletedAt { get; set; } }
            public sealed class AcmeDb(DbContextOptions<AcmeDb> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
            {
                public DbSet<Note> Notes => Set<Note>();
                protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<Note>();
                public IQueryable<Note> WithDeleted(TimeProvider time) =>
                    Notes.IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(n => n.Id != Ids.New() && n.DeletedAt < time.GetUtcNow());
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Entities_of_extension_contexts_must_be_tenant_owned()
    {
        var diagnostics = await AnalyzeAsync(Registered + """
            public sealed class Shared { public Guid Id { get; set; } }
            public sealed class Other { public Guid Id { get; set; } }
            public sealed class AcmeDb(DbContextOptions<AcmeDb> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
            {
                public DbSet<Shared> Shared => Set<Shared>();
                protected override void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<Other>();
            }
            """);

        Assert.Equal(["PDN1001", "PDN1001"], diagnostics);
    }

    [Fact]
    public async Task Disabling_the_tenant_filter_and_raw_sql_are_errors()
    {
        var diagnostics = await AnalyzeAsync(Registered + """
            public sealed class Note : ITenantOwned { public Guid Id { get; set; } public Guid TenantId { get; set; } }
            public sealed class AcmeDb(DbContextOptions<AcmeDb> options, ITenantContext tenant) : ExtensionDbContext(options, tenant)
            {
                public DbSet<Note> Notes => Set<Note>();
                protected override void ConfigureModel(ModelBuilder modelBuilder) { }
                public void Bad()
                {
                    _ = Notes.IgnoreQueryFilters();
                    _ = Notes.IgnoreQueryFilters([QueryFilters.Tenant]);
                    _ = Notes.IgnoreQueryFilters(["Tenant", QueryFilters.SoftDelete]);
                    _ = Notes.FromSqlRaw("SELECT * FROM notes");
                    Database.ExecuteSqlRaw("DELETE FROM notes");
                }
            }
            """);

        Assert.Equal(["PDN1002", "PDN1002", "PDN1002", "PDN1003", "PDN1003"], diagnostics);
    }

    [Fact]
    public async Task Unregistered_extensions_and_convention_breaks_are_warnings()
    {
        var diagnostics = await AnalyzeAsync("""
            namespace Acme;
            public sealed class Forgotten : IExtension
            {
                public void Configure(IExtensionBuilder builder)
                {
                    _ = Guid.NewGuid();
                    _ = DateTime.UtcNow;
                    _ = DateTimeOffset.Now;
                }
            }
            """);

        Assert.Equal(["PDN1004", "PDN1005", "PDN1005", "PDN1006"], diagnostics);
    }
}
