using System.Text;

namespace PaperDotNet.Dav.Features;

/// <summary>
/// Names in the WebDAV tree (ADR-0047): titles, made safe for Windows; a file adds the extension of its current file.
/// Siblings with the same name (case-insensitive) keep it in creation order (UUIDv7 ids): the oldest has the name, the
/// others get " (2)", " (3)", … before the extension, so existing names stay stable when duplicates appear.
/// </summary>
internal static class DavNames
{
    public const int MaxLength = 255;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> ExtensionByMediaType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["image/tiff"] = ".tiff",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    /// <summary>The extension of the stored file name, or the usual one for its media type.</summary>
    public static string Extension(string fileName, string mediaType)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 1 && extension.Length <= 16 && Sanitize(extension) == extension
            ? extension
            : ExtensionByMediaType.GetValueOrDefault(mediaType, string.Empty);
    }

    /// <summary>
    /// <c>\ / : * ? " &lt; &gt; |</c> and control characters become <c>_</c>, outer spaces and trailing dots are removed,
    /// reserved device names get a <c>_</c>, and the name fits in 255 characters with its extension.
    /// </summary>
    public static string Name(string? title, string extension = "")
    {
        var stem = Sanitize(title ?? string.Empty);
        if (stem.Length == 0)
        {
            stem = "_";
        }

        if (Reserved.Contains(stem))
        {
            stem += "_";
        }

        var max = MaxLength - extension.Length;
        return (stem.Length > max ? stem[..max].TrimEnd('.', ' ') : stem) + extension;
    }

    /// <summary>Unique names for siblings, ordered by id (creation order).</summary>
    public static List<(T Entry, string Name)> Assign<T>(IEnumerable<T> siblings, Func<T, Guid> id, Func<T, string> name)
    {
        var ordered = siblings.Select(s => (Entry: s, Name: name(s))).OrderBy(s => id(s.Entry)).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = new HashSet<int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (used.Add(ordered[i].Name))
            {
                first.Add(i);
            }
        }

        var result = new List<(T, string)>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (entry, baseName) = ordered[i];
            if (first.Contains(i))
            {
                result.Add((entry, baseName));
                continue;
            }

            var extension = Path.GetExtension(baseName);
            var stem = baseName[..^extension.Length];
            string candidate;
            var n = 2;
            do
            {
                var suffix = $" ({n++})";
                var room = MaxLength - extension.Length - suffix.Length;
                candidate = (stem.Length > room ? stem[..room] : stem) + suffix + extension;
            }
            while (!used.Add(candidate));

            result.Add((entry, candidate));
        }

        return result;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }

        return builder.ToString().Trim().TrimEnd('.', ' ');
    }
}
