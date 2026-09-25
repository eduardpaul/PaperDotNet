using System.Security.Cryptography;
using System.Text;
using PaperDotNet.Search.Contracts;

namespace PaperDotNet.Search.Features;

/// <summary>A window of a document's text and the page it is on (null: not on a page).</summary>
internal sealed record PassageText(int? Page, string Text);

/// <summary>
/// Splits documents into passages (SRC-07, SRC-09): the title, keywords and body first, then each page on its own,
/// in windows of about <see cref="MaxChars"/> characters that overlap by <see cref="Overlap"/> and end at word
/// boundaries, so a sentence cut by one window is whole in the next.
/// </summary>
internal static class Passages
{
    public const int MaxChars = 1200;
    public const int Overlap = 150;
    public const int MaxPerDocument = 400;

    public static List<PassageText> Split(SearchDocumentData document)
    {
        var passages = new List<PassageText>();
        var header = Normalize($"{document.Keywords}\n{document.Body}");
        if (header.Length == 0 && document.Pages.Count == 0)
        {
            header = Normalize(document.Title);
        }

        passages.AddRange(Windows(header).Select(t => new PassageText(null, t)));
        for (var i = 0; i < document.Pages.Count && passages.Count < MaxPerDocument; i++)
        {
            passages.AddRange(Windows(Normalize(document.Pages[i])).Select(t => new PassageText(i + 1, t)));
        }

        return passages.Count > MaxPerDocument ? passages[..MaxPerDocument] : passages;
    }

    /// <summary>What is embedded for a passage: the title gives context to every passage of the document.</summary>
    public static string EmbeddingInput(string title, string text) => $"{title}\n\n{text}";

    public static string Hash(string input) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    /// <summary>Windows of <paramref name="text"/> (already normalized).</summary>
    internal static IEnumerable<string> Windows(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + MaxChars);
            if (end < text.Length)
            {
                var space = text.LastIndexOf(' ', end, end - start);
                if (space > start + (MaxChars / 2))
                {
                    end = space;
                }
            }

            yield return text[start..end].Trim();
            if (end >= text.Length)
            {
                yield break;
            }

            // Step back by the overlap, to the start of a word.
            var next = Math.Max(start + 1, end - Overlap);
            var boundary = text.IndexOf(' ', next);
            start = boundary >= 0 && boundary < end ? boundary + 1 : end;
        }
    }

    /// <summary>Whitespace runs become one space.</summary>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
