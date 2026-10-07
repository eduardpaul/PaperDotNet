using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using FubarDev.WebDavServer.Models;

namespace FubarDev.WebDavServer.Parsing;

/// <summary>
/// Parses the WebDAV header grammar of RFC 4918 (<c>If</c>, entity tags, <c>Lock-Token</c>). Replaces the upstream
/// Yoakke-generated lexer and parser (nightly packages) with the same tokens and rules.
/// </summary>
public static partial class HeaderParser
{
    private enum TokenKind
    {
        WeakEntityTag,
        QuotedString,
        Comma,
        LeftParen,
        RightParen,
        UrlReference,
        LeftBracket,
        RightBracket,
        Not,
    }

    /// <summary>Parses an <c>If</c> header: one or more no-tag lists, or one or more tagged lists.</summary>
    public static bool TryParseIfHeader(string value, [NotNullWhen(true)] out IfHeader? header)
    {
        header = null;
        if (!TryTokenize(value, out var tokens) || tokens.Count == 0)
        {
            return false;
        }

        var position = 0;
        if (tokens[0].Kind == TokenKind.LeftParen)
        {
            var lists = new List<IfNoTagList>();
            while (position < tokens.Count && TryParseConditionList(tokens, ref position, out var list))
            {
                lists.Add(new IfNoTagList(list));
            }

            header = position == tokens.Count && lists.Count > 0 ? new IfHeader(lists) : null;
            return header is not null;
        }

        var tagged = new List<IfTaggedList>();
        while (position < tokens.Count)
        {
            if (tokens[position].Kind != TokenKind.UrlReference
                || !Uri.TryCreate(Inner(tokens[position].Text), UriKind.RelativeOrAbsolute, out var resource))
            {
                return false;
            }

            position++;
            var lists = new List<IfList>();
            while (position < tokens.Count && tokens[position].Kind == TokenKind.LeftParen)
            {
                if (!TryParseConditionList(tokens, ref position, out var list))
                {
                    return false;
                }

                lists.Add(list);
            }

            if (lists.Count == 0)
            {
                return false;
            }

            tagged.Add(new IfTaggedList(resource, lists));
        }

        header = new IfHeader(tagged);
        return true;
    }

    /// <summary>Parses a comma-separated list of entity tags (<c>"a", W/"b"</c>).</summary>
    public static bool TryParseEntityTagList(string value, [NotNullWhen(true)] out IReadOnlyList<EntityTag>? entityTags)
    {
        entityTags = null;
        if (!TryTokenize(value, out var tokens) || tokens.Count == 0)
        {
            return false;
        }

        var result = new List<EntityTag>();
        var position = 0;
        while (true)
        {
            if (!TryParseEntityTag(tokens, ref position, out var entityTag))
            {
                return false;
            }

            result.Add(entityTag);
            if (position == tokens.Count)
            {
                entityTags = result;
                return true;
            }

            if (tokens[position++].Kind != TokenKind.Comma)
            {
                return false;
            }
        }
    }

    /// <summary>Parses a coded URL (<c>&lt;urn:uuid:…&gt;</c>), e.g. a <c>Lock-Token</c> header.</summary>
    public static bool TryParseCodedUrl(string value, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        return TryTokenize(value, out var tokens)
               && tokens.Count == 1
               && tokens[0].Kind == TokenKind.UrlReference
               && Uri.TryCreate(Inner(tokens[0].Text), UriKind.Absolute, out url);
    }

    private static bool TryParseConditionList(List<Token> tokens, ref int position, [NotNullWhen(true)] out IfList? list)
    {
        list = null;
        if (tokens[position].Kind != TokenKind.LeftParen)
        {
            return false;
        }

        position++;
        var conditions = new List<IfCondition>();
        while (position < tokens.Count && tokens[position].Kind != TokenKind.RightParen)
        {
            var not = false;
            if (tokens[position].Kind == TokenKind.Not)
            {
                not = true;
                position++;
                if (position == tokens.Count)
                {
                    return false;
                }
            }

            switch (tokens[position].Kind)
            {
                case TokenKind.UrlReference:
                    if (!Uri.TryCreate(Inner(tokens[position].Text), UriKind.Absolute, out var stateToken))
                    {
                        return false;
                    }

                    position++;
                    conditions.Add(new IfCondition(not, stateToken, null));
                    break;
                case TokenKind.LeftBracket:
                    position++;
                    if (!TryParseEntityTag(tokens, ref position, out var entityTag)
                        || position == tokens.Count || tokens[position].Kind != TokenKind.RightBracket)
                    {
                        return false;
                    }

                    position++;
                    conditions.Add(new IfCondition(not, null, entityTag));
                    break;
                default:
                    return false;
            }
        }

        if (position == tokens.Count || conditions.Count == 0)
        {
            return false;
        }

        position++;
        list = new IfList(conditions);
        return true;
    }

    private static bool TryParseEntityTag(List<Token> tokens, ref int position, out EntityTag entityTag)
    {
        entityTag = default;
        if (position == tokens.Count)
        {
            return false;
        }

        var token = tokens[position];
        switch (token.Kind)
        {
            case TokenKind.QuotedString:
                entityTag = new EntityTag(false, Unescape(Inner(token.Text)));
                break;
            case TokenKind.WeakEntityTag:
                entityTag = new EntityTag(true, Unescape(token.Text[3..^1]));
                break;
            default:
                return false;
        }

        position++;
        return true;
    }

    private static bool TryTokenize(string value, out List<Token> tokens)
    {
        tokens = [];
        var position = 0;
        while (position < value.Length)
        {
            var match = TokenPattern().Match(value, position);
            if (!match.Success || match.Length == 0)
            {
                return false;
            }

            position += match.Length;
            if (match.Groups["ws"].Success)
            {
                continue;
            }

            var kind = Enum.GetValues<TokenKind>().First(k => match.Groups[k.ToString()].Success);
            tokens.Add(new Token(kind, match.Value));
        }

        return true;
    }

    private static string Inner(string text) => text[1..^1];

    private static string Unescape(string value) =>
        EscapePattern().Replace(value, match => match.Value[0] == '\\' ? Regex.Unescape(match.Value) : "\"");

    /// <summary>The upstream tokens, in their priority order. Whitespace is skipped.</summary>
    [GeneratedRegex("""
        \G(?:
          (?<ws>\s+)
        | (?<WeakEntityTag>[Ww]/"((\\[^\n\r])|[^\r\n\\"]|(""))*")
        | (?<QuotedString>"((\\[^\n\r])|[^\r\n\\"]|(""))*")
        | (?<Comma>,)
        | (?<LeftParen>\()
        | (?<RightParen>\))
        | (?<UrlReference><[^>]+>)
        | (?<LeftBracket>\[)
        | (?<RightBracket>\])
        | (?<Not>[Nn][Oo][Tt])
        )
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.ExplicitCapture)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("""(\\.)|("")""")]
    private static partial Regex EscapePattern();

    private sealed record Token(TokenKind Kind, string Text);
}
