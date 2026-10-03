using PaperDotNet.Abstractions;

namespace PaperDotNet.Search.Zvec;

/// <summary>
/// A parsed query in zvec's full-text syntax. zvec has no prefix terms (spike S3), so a group that is a single
/// <c>word*</c> becomes a trigram condition on the <c>prefix</c> column (<see cref="Prefixes"/>; it matches the word
/// anywhere, not only at a word start). Other prefix terms (in OR groups, phrases, or shorter than three letters)
/// are searched as whole words.
/// </summary>
/// <param name="Text">Required and excluded terms for the text columns, or null when only prefixes are positive.</param>
/// <param name="Prefixes">One trigram query per prefix group, all required.</param>
/// <param name="Excluded">The excluded terms as one OR query, or null.</param>
internal sealed record ZvecFullText(string? Text, IReadOnlyList<string> Prefixes, string? Excluded)
{
    /// <param name="query">The parsed query.</param>
    /// <param name="trigrams">Whether the collection has the trigram column; without it prefix terms are whole words.</param>
    public static ZvecFullText From(FullTextQuery query, bool trigrams = true)
    {
        var groups = new List<string>();
        var prefixes = new List<string>();
        foreach (var group in query.Groups)
        {
            if (trigrams && group is [{ Prefix: true, Tokens: [var token] }] && token.Length >= 3)
            {
                prefixes.Add(string.Join(" AND ", Trigrams(token)));
                continue;
            }

            var terms = group.Select(Term).ToList();
            groups.Add(terms.Count == 1 ? terms[0] : $"({string.Join(" OR ", terms)})");
        }

        var excluded = query.Excluded.Select(Term).ToList();
        string? text = null;
        if (groups.Count > 0)
        {
            text = string.Join(" AND ", groups);
            if (excluded.Count > 0)
            {
                text += " AND " + string.Join(" AND ", excluded.Select(e => $"NOT {e}"));
            }
        }

        return new ZvecFullText(text, prefixes, excluded.Count == 0 ? null : string.Join(" OR ", excluded));
    }

    /// <summary>A term: a word, or a phrase in quotes. Tokens are letters and digits only, so they need no escaping.</summary>
    private static string Term(FullTextTerm term) => term.Tokens.Count == 1 ? term.Tokens[0] : $"\"{string.Join(' ', term.Tokens)}\"";

    private static IEnumerable<string> Trigrams(string token)
    {
        for (var i = 0; i + 3 <= token.Length; i++)
        {
            yield return token.Substring(i, 3);
        }
    }
}
