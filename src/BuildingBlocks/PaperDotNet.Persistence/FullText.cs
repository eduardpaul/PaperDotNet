using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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

/// <summary>A search term: one token, or a phrase of several; the last token may be a prefix.</summary>
public sealed record FullTextTerm(IReadOnlyList<string> Tokens, bool Prefix);

/// <summary>
/// A parsed search query: every group must match (AND), a group matches when any
/// of its terms matches (OR), and no excluded term may match. Tokens are
/// lower-case letters and digits only, so rendering them needs no escaping.
/// </summary>
public sealed record FullTextQuery(IReadOnlyList<IReadOnlyList<FullTextTerm>> Groups, IReadOnlyList<FullTextTerm> Excluded)
{
    public const int MaxTerms = 32;

    /// <summary>
    /// Parses user syntax: <c>words</c> (all must match), <c>"exact phrase"</c>,
    /// <c>a OR b</c>, <c>-word</c> or <c>NOT word</c>, and <c>prefix*</c>.
    /// Returns null with an error when nothing positive remains.
    /// </summary>
    public static FullTextQuery? Parse(string? text, out string? error)
    {
        var groups = new List<List<FullTextTerm>>();
        var excluded = new List<FullTextTerm>();
        var joinOr = false;
        var negateNext = false;
        var count = 0;
        foreach (var (raw, quoted) in Split(text ?? string.Empty))
        {
            if (!quoted && raw == "OR")
            {
                joinOr = groups.Count > 0;
                continue;
            }

            if (!quoted && raw == "NOT")
            {
                negateNext = true;
                continue;
            }

            var value = raw;
            var negate = negateNext;
            negateNext = false;
            if (!quoted && value.StartsWith('-'))
            {
                negate = true;
                value = value[1..];
            }

            var prefix = value.EndsWith('*');
            var tokens = Tokenize(value);
            if (tokens.Count == 0 || ++count > MaxTerms)
            {
                joinOr = false;
                continue;
            }

            var term = new FullTextTerm(tokens, prefix);
            if (negate)
            {
                excluded.Add(term);
            }
            else if (joinOr)
            {
                groups[^1].Add(term);
            }
            else
            {
                groups.Add([term]);
            }

            joinOr = false;
        }

        if (groups.Count == 0)
        {
            error = "Enter at least one word to search for (exclusions alone are not enough).";
            return null;
        }

        error = null;
        return new FullTextQuery(groups, excluded);
    }

    /// <summary>All positive tokens (for highlighting).</summary>
    public IEnumerable<string> PositiveTokens => Groups.SelectMany(g => g).SelectMany(t => t.Tokens);

    /// <summary>Lower-case runs of letters and digits, matching how both providers tokenize.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static IEnumerable<(string Value, bool Quoted)> Split(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                end = end < 0 ? text.Length : end;
                var phrase = text[(i + 1)..end];

                // A * right after the closing quote makes the phrase a prefix.
                var star = end + 1 < text.Length && text[end + 1] == '*';
                yield return (star ? phrase + "*" : phrase, true);
                i = end + (star ? 2 : 1);
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"')
            {
                i++;
            }

            yield return (text[start..i], false);
        }
    }
}
