using System.Text;
using System.Text.RegularExpressions;

namespace PaperDotNet.Notes.Features;

/// <summary>A <c>[[wiki link]]</c>: <c>[[Target#Heading|Alias]]</c>, or <c>![[Target]]</c> to embed.</summary>
internal sealed record WikiLink(string Target, string? Heading, string? Alias, bool Embed);

/// <summary>
/// What notes take from their Markdown (LST-18), with the conventions of Obsidian: <c>#tags</c> (letters, digits,
/// <c>_</c>, <c>-</c> and <c>/</c> for nesting; not only digits) and <c>[[wiki links]]</c>. Code spans and fenced code
/// blocks are ignored.
/// </summary>
internal static partial class NoteMarkdown
{
    public const int MaxLinks = 500;
    public const int MaxTags = 100;

    [GeneratedRegex(@"```.*?(```|\z)|~~~.*?(~~~|\z)|`[^`\n]*`", RegexOptions.Singleline)]
    private static partial Regex Code();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_/&#\]\[])#(?=[\p{L}\p{N}_/\-]*[\p{L}_])([\p{L}\p{N}_][\p{L}\p{N}_/\-]*)")]
    private static partial Regex Tag();

    [GeneratedRegex(@"(!?)\[\[([^\[\]\|#\^\n]+)(?:#([^\[\]\|\n]*))?(?:\|([^\[\]\n]*))?\]\]")]
    private static partial Regex Link();

    /// <summary>The tags of the text, without <c>#</c>, in order of appearance, each once (case-insensitive).</summary>
    public static List<string> Tags(string markdown)
    {
        var text = Link().Replace(Code().Replace(markdown, " "), " ");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();
        foreach (Match match in Tag().Matches(text))
        {
            var tag = match.Groups[1].Value.TrimEnd('/', '-');
            if (tag.Length > 0 && seen.Add(tag) && tags.Count < MaxTags)
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>The wiki links of the text, in order.</summary>
    public static List<WikiLink> Links(string markdown) =>
        [.. Link().Matches(Code().Replace(markdown, " "))
            .Select(m => new WikiLink(m.Groups[2].Value.Trim(), Empty(m.Groups[3].Value), Empty(m.Groups[4].Value), m.Groups[1].Value == "!"))
            .Where(l => l.Target.Length > 0)
            .Take(MaxLinks)];

    /// <summary>
    /// How a title or link target is compared: the last path segment (<c>Folder/Note</c> is <c>Note</c>), without
    /// <c>.md</c>, trimmed and lower-case.
    /// </summary>
    public static string Normalize(string title)
    {
        var name = title.Trim();
        var slash = name.LastIndexOf('/');
        name = slash >= 0 ? name[(slash + 1)..] : name;
        name = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>Points the links to <paramref name="oldTitle"/> at <paramref name="newTitle"/>, keeping headings and aliases; code is left alone.</summary>
    public static string RenameLinks(string markdown, string oldTitle, string newTitle)
    {
        var old = Normalize(oldTitle);
        var result = new StringBuilder(markdown.Length);
        var position = 0;
        foreach (Match code in Code().Matches(markdown))
        {
            result.Append(Rename(markdown[position..code.Index], old, newTitle)).Append(code.Value);
            position = code.Index + code.Length;
        }

        return result.Append(Rename(markdown[position..], old, newTitle)).ToString();
    }

    private static string Rename(string text, string oldNormalized, string newTitle) =>
        Link().Replace(text, m => Normalize(m.Groups[2].Value) != oldNormalized
            ? m.Value
            : $"{m.Groups[1].Value}[[{newTitle}{(m.Groups[3].Success ? "#" + m.Groups[3].Value : string.Empty)}{(m.Groups[4].Success ? "|" + m.Groups[4].Value : string.Empty)}]]");

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
