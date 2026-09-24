using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace PaperDotNet.Extensions.Analyzers;

/// <summary>
/// Platform rules for extension code (EXT-05). Active in projects that reference the extension
/// SDK; the same rules the host's architecture tests enforce for its own modules.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExtensionAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "PaperDotNet.Extensions";
    private const string ExtensionInterface = "PaperDotNet.Extensions.IExtension";
    private const string ExtensionAttribute = "PaperDotNet.Extensions.PaperDotNetExtensionAttribute";
    private const string ExtensionDbContext = "PaperDotNet.Extensions.ExtensionDbContext";
    private const string TenantOwned = "PaperDotNet.Abstractions.ITenantOwned";
    private const string TenantFilter = "Tenant";

    private static readonly HashSet<string> RawSqlMethods =
    [
        "FromSql", "FromSqlRaw", "FromSqlInterpolated",
        "ExecuteSql", "ExecuteSqlAsync", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
        "SqlQuery", "SqlQueryRaw",
    ];

    public static readonly DiagnosticDescriptor EntityNotTenantOwned = new(
        "PDN1001",
        "Extension entities must be tenant-owned",
        "Entity '{0}' of {1} must implement ITenantOwned: extension data lives inside the tenant's isolation boundary",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TenantFilterDisabled = new(
        "PDN1002",
        "The tenant query filter must not be disabled",
        "IgnoreQueryFilters must name the filters to skip, such as QueryFilters.SoftDelete, and never the Tenant filter",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RawSql = new(
        "PDN1003",
        "No raw SQL in extensions",
        "'{0}' runs raw SQL: use LINQ so the code works on SQLite and PostgreSQL and keeps the tenant filter",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnregisteredExtension = new(
        "PDN1004",
        "Extension is not registered",
        "'{0}' implements IExtension but the assembly has no [assembly: PaperDotNetExtension(typeof({0}))]; the host will not find it",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor UseTimeProvider = new(
        "PDN1005",
        "Use TimeProvider",
        "Use TimeProvider.GetUtcNow() instead of {0}: tests control time through TimeProvider",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UseIds = new(
        "PDN1006",
        "Use Ids.New()",
        "Use Ids.New() instead of Guid.NewGuid(): time-ordered ids (UUIDv7) keep indexes and paging efficient",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(EntityNotTenantOwned, TenantFilterDisabled, RawSql, UnregisteredExtension, UseTimeProvider, UseIds);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var extension = start.Compilation.GetTypeByMetadataName(ExtensionInterface);
            if (extension is null)
            {
                return;
            }

            var symbols = new Symbols(
                extension,
                start.Compilation.GetTypeByMetadataName(ExtensionDbContext),
                start.Compilation.GetTypeByMetadataName(TenantOwned),
                start.Compilation.GetTypeByMetadataName("System.Guid"),
                start.Compilation.GetTypeByMetadataName("System.DateTime"),
                start.Compilation.GetTypeByMetadataName("System.DateTimeOffset"));

            var extensions = new ConcurrentBag<INamedTypeSymbol>();
            start.RegisterSymbolAction(c => AnalyzeType(c, symbols, extensions), SymbolKind.NamedType);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, symbols), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzePropertyReference(c, symbols), OperationKind.PropertyReference);
            start.RegisterCompilationEndAction(c => ReportUnregistered(c, extensions));
        });
    }

    private static void AnalyzeType(SymbolAnalysisContext context, Symbols symbols, ConcurrentBag<INamedTypeSymbol> extensions)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind == TypeKind.Class && !type.IsAbstract && type.AllInterfaces.Contains(symbols.Extension, SymbolEqualityComparer.Default))
        {
            extensions.Add(type);
        }

        if (symbols.ExtensionDbContext is null || !InheritsFrom(type, symbols.ExtensionDbContext))
        {
            return;
        }

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.Type is INamedTypeSymbol { Name: "DbSet", TypeArguments.Length: 1 } dbSet
                && !IsTenantOwned(dbSet.TypeArguments[0], symbols))
            {
                context.ReportDiagnostic(Diagnostic.Create(EntityNotTenantOwned, property.Locations.FirstOrDefault(), dbSet.TypeArguments[0].Name, type.Name));
            }
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, Symbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var inEfCore = method.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", System.StringComparison.Ordinal) == true;

        if (inEfCore && method.Name == "IgnoreQueryFilters" && DisablesTenantFilter(invocation))
        {
            context.ReportDiagnostic(Diagnostic.Create(TenantFilterDisabled, invocation.Syntax.GetLocation()));
        }
        else if (inEfCore && RawSqlMethods.Contains(method.Name))
        {
            context.ReportDiagnostic(Diagnostic.Create(RawSql, invocation.Syntax.GetLocation(), method.Name));
        }
        else if (method.Name == "NewGuid" && SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.Guid))
        {
            context.ReportDiagnostic(Diagnostic.Create(UseIds, invocation.Syntax.GetLocation()));
        }
        else if (method.Name == "Entity" && method.IsGenericMethod && method.ContainingType?.Name == "ModelBuilder"
                 && symbols.ExtensionDbContext is not null
                 && context.ContainingSymbol.ContainingType is { } owner && InheritsFrom(owner, symbols.ExtensionDbContext)
                 && !IsTenantOwned(method.TypeArguments[0], symbols))
        {
            context.ReportDiagnostic(Diagnostic.Create(EntityNotTenantOwned, invocation.Syntax.GetLocation(), method.TypeArguments[0].Name, owner.Name));
        }
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context, Symbols symbols)
    {
        var property = ((IPropertyReferenceOperation)context.Operation).Property;
        if (property.IsStatic
            && property.Name is "Now" or "UtcNow" or "Today"
            && (SymbolEqualityComparer.Default.Equals(property.ContainingType, symbols.DateTime)
                || SymbolEqualityComparer.Default.Equals(property.ContainingType, symbols.DateTimeOffset)))
        {
            context.ReportDiagnostic(Diagnostic.Create(UseTimeProvider, context.Operation.Syntax.GetLocation(), $"{property.ContainingType.Name}.{property.Name}"));
        }
    }

    private static void ReportUnregistered(CompilationAnalysisContext context, ConcurrentBag<INamedTypeSymbol> extensions)
    {
        var registered = context.Compilation.Assembly.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == ExtensionAttribute && a.ConstructorArguments.Length == 1)
            .Select(a => a.ConstructorArguments[0].Value)
            .OfType<INamedTypeSymbol>()
            .ToList();
        foreach (var type in extensions.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            if (!registered.Contains(type, SymbolEqualityComparer.Default))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnregisteredExtension, type.Locations.FirstOrDefault(), type.Name));
            }
        }
    }

    /// <summary>No filter names (all filters), or the Tenant filter among them.</summary>
    private static bool DisablesTenantFilter(IInvocationOperation invocation)
    {
        var filters = invocation.Arguments.Where(a => a.Parameter?.Type is IArrayTypeSymbol or INamedTypeSymbol { Name: "IReadOnlyCollection" or "IEnumerable" }).ToList();
        if (filters.Count == 0)
        {
            return invocation.Arguments.Length <= 1;
        }

        return filters.Any(argument => argument.Value.Descendants().Prepend(argument.Value)
            .Any(o => o.ConstantValue is { HasValue: true, Value: TenantFilter }));
    }

    private static bool IsTenantOwned(ITypeSymbol type, Symbols symbols) =>
        symbols.TenantOwned is null || type.AllInterfaces.Contains(symbols.TenantOwned, SymbolEqualityComparer.Default);

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Symbols(
        INamedTypeSymbol extension,
        INamedTypeSymbol? extensionDbContext,
        INamedTypeSymbol? tenantOwned,
        INamedTypeSymbol? guid,
        INamedTypeSymbol? dateTime,
        INamedTypeSymbol? dateTimeOffset)
    {
        public INamedTypeSymbol Extension { get; } = extension;

        public INamedTypeSymbol? ExtensionDbContext { get; } = extensionDbContext;

        public INamedTypeSymbol? TenantOwned { get; } = tenantOwned;

        public INamedTypeSymbol? Guid { get; } = guid;

        public INamedTypeSymbol? DateTime { get; } = dateTime;

        public INamedTypeSymbol? DateTimeOffset { get; } = dateTimeOffset;
    }
}
