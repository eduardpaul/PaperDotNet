using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaperDotNet.Abstractions;

namespace PaperDotNet.Persistence;

/// <summary>
/// Provider-neutral full-text search (SRC-01/02). Entities opt in with
/// <see cref="FullTextExtensions.HasFullTextIndex{TEntity}"/>; each database
/// provider builds the index (PostgreSQL: weighted <c>tsvector</c> + GIN,
/// SQLite: FTS5) and implements <see cref="IFullTextSearch"/>.
/// </summary>
public interface IFullTextSearch
{
    /// <summary>
    /// Ids and ranks (higher is better) of <typeparamref name="TEntity"/> rows matching
    /// <paramref name="query"/>. Composable: join it with the entity set, which applies
    /// the tenant filter.
    /// </summary>
    IQueryable<FullTextMatch> Match<TEntity>(DbContext db, FullTextQuery query)
        where TEntity : class;
}

/// <summary>One full-text hit (a keyless type in models with a full-text index).</summary>
public sealed class FullTextMatch
{
    /// <summary>Column names provider queries must return.</summary>
    public const string IdColumn = "id";
    public const string RankColumn = "rank";

    public Guid Id { get; set; }

    public double Rank { get; set; }
}

public static class FullTextExtensions
{
    /// <summary>Model annotation: the full-text columns (property names, most important first).</summary>
    public const string Annotation = "PaperDotNet:FullText";

    /// <summary>
    /// Indexes <paramref name="properties"/> (text, most important first: the first
    /// gets the highest weight) for full-text search. The entity needs a <c>Guid Id</c>.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasFullTextIndex<TEntity>(this EntityTypeBuilder<TEntity> entity, params string[] properties)
        where TEntity : class =>
        entity.HasAnnotation(Annotation, string.Join(',', properties));

    /// <summary>Model annotation: the property holding each row's text language (<see cref="FullTextLanguages"/>).</summary>
    public const string LanguageAnnotation = "PaperDotNet:FullTextLanguage";

    /// <summary>
    /// Stems the indexed text in the row's language (SRC-05): <paramref name="property"/> holds a name from
    /// <see cref="FullTextLanguages.All"/> or null. PostgreSQL stems per row; SQLite stems English for all rows.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasFullTextLanguage<TEntity>(this EntityTypeBuilder<TEntity> entity, string property)
        where TEntity : class =>
        entity.HasAnnotation(LanguageAnnotation, property);
}

/// <summary>Languages with stemming support (the PostgreSQL text search configuration names).</summary>
public static class FullTextLanguages
{
    public static readonly IReadOnlyList<string> All =
    [
        "danish", "dutch", "english", "finnish", "french", "german", "hungarian", "italian",
        "norwegian", "portuguese", "romanian", "russian", "spanish", "swedish", "turkish",
    ];

    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["da"] = "danish",
        ["dan"] = "danish",
        ["nl"] = "dutch",
        ["nld"] = "dutch",
        ["en"] = "english",
        ["eng"] = "english",
        ["fi"] = "finnish",
        ["fin"] = "finnish",
        ["fr"] = "french",
        ["fra"] = "french",
        ["de"] = "german",
        ["deu"] = "german",
        ["hu"] = "hungarian",
        ["hun"] = "hungarian",
        ["it"] = "italian",
        ["ita"] = "italian",
        ["no"] = "norwegian",
        ["nb"] = "norwegian",
        ["nor"] = "norwegian",
        ["pt"] = "portuguese",
        ["por"] = "portuguese",
        ["ro"] = "romanian",
        ["ron"] = "romanian",
        ["ru"] = "russian",
        ["rus"] = "russian",
        ["es"] = "spanish",
        ["spa"] = "spanish",
        ["sv"] = "swedish",
        ["swe"] = "swedish",
        ["tr"] = "turkish",
        ["tur"] = "turkish",
    };

    /// <summary>The language for an ISO 639-1 or 639-2 code (e.g. <c>de</c>, <c>deu</c>; Tesseract uses 639-2), or null.</summary>
    public static string? FromCode(string? code) =>
        code is null ? null : Codes.GetValueOrDefault(code) ?? (All.Contains(code.ToLowerInvariant()) ? code.ToLowerInvariant() : null);
}
