using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PaperDotNet.Persistence;

/// <summary>
/// Provider-specific JSON document predicates, used when translating queries
/// over JSON columns (e.g. list item fields). Implemented by the database
/// provider project so modules stay provider-agnostic.
/// </summary>
public interface IJsonQueryFunctions
{
    /// <summary>True when <paramref name="document"/> contains the JSON fragment (PostgreSQL <c>@&gt;</c>, index-friendly).</summary>
    Expression Contains(Expression document, string jsonFragment);

    /// <summary>True when the top-level property exists in <paramref name="document"/>.</summary>
    Expression HasProperty(Expression document, string propertyName);
}

public static class JsonIndexExtensions
{
    private static readonly string[] JsonPathOps = ["jsonb_path_ops"];

    /// <summary>
    /// Marks an index on a JSON document column as a containment index
    /// (PostgreSQL: GIN with <c>jsonb_path_ops</c>). Other providers ignore the hint.
    /// </summary>
    public static IndexBuilder IsJsonContainmentIndex(this IndexBuilder index) =>
        index
            .HasAnnotation("Npgsql:IndexMethod", "gin")
            .HasAnnotation("Npgsql:IndexOperators", JsonPathOps);
}
