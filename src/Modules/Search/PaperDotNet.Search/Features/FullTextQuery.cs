using System.Text;

namespace PaperDotNet.Search.Features;

/// <summary>A search term: one token, or a phrase of several; the last token may be a prefix.</summary>
internal sealed record FullTextTerm(IReadOnlyList<string> Tokens, bool Prefix);

/// <summary>
/// A parsed search query: every group must match (AND), a group matches when any of its terms matches (OR), and no
/// excluded term may match. Tokens are lower-case letters and digits only, so rendering them needs no escaping.
/// </summary>
internal sealed record FullTextQuery(IReadOnlyList<IReadOnlyList<FullTextTerm>> Groups, IReadOnlyList<FullTextTerm> Excluded)
{
    public const int MaxTerms = 32;

    /// <summary>
    /// Parses user syntax: <c>words</c> (all must match), <c>"exact phrase"</c>, <c>a OR b</c>, <c>-word</c> or
    /// <c>NOT word</c>, and <c>prefix*</c>. Returns null with an error when nothing positive remains.
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

    /// <summary>All positive tokens (for snippets).</summary>
    public IEnumerable<string> PositiveTokens => Groups.SelectMany(g => g).SelectMany(t => t.Tokens);

    /// <summary>The query in FTS5 syntax: <c>AND</c>, <c>OR</c>, <c>NOT</c>, <c>"phrases"</c>, <c>"prefix"*</c>.</summary>
    public string ToFts5()
    {
        static string Term(FullTextTerm term) => $"\"{string.Join(' ', term.Tokens)}\"{(term.Prefix ? "*" : string.Empty)}";
        var positive = string.Join(" AND ", Groups.Select(g => g.Count == 1 ? Term(g[0]) : $"({string.Join(" OR ", g.Select(Term))})"));
        return Excluded.Count == 0 ? positive : $"({positive}) NOT ({string.Join(" OR ", Excluded.Select(Term))})";
    }

    /// <summary>Lower-case runs of letters and digits, matching how the full-text index tokenizes.</summary>
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
