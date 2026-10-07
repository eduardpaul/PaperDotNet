using System.Text;

namespace WebDavPoc;

/// <summary>The name rules of the plan: title + extension, Windows-safe, duplicates suffixed in creation order.</summary>
public static class DavNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> ExtensionByMediaType = new()
    {
        ["application/pdf"] = ".pdf",
        ["image/tiff"] = ".tiff",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
    };

    public static string Extension(Node file)
    {
        var ext = Path.GetExtension(file.FileName ?? "");
        return ext.Length > 1 ? ext : ExtensionByMediaType.GetValueOrDefault(file.MediaType, "");
    }

    public static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c < 32 || "\\/:*?\"<>|".Contains(c) ? '_' : c);
        }

        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        return result.Length == 0 ? "_" : result;
    }

    public static string BaseName(Node node)
    {
        var ext = node.Kind == NodeKind.File ? Extension(node) : "";
        var stem = Sanitize(node.Title);
        if (Reserved.Contains(stem))
        {
            stem += "_";
        }

        var max = 255 - ext.Length;
        return (stem.Length > max ? stem[..max] : stem) + ext;
    }

    /// <summary>Unique, stable names for siblings: the oldest keeps the name, the others get " (2)", " (3)", ….</summary>
    public static IReadOnlyList<(Node Node, string Name)> Assign(IEnumerable<Node> siblings)
    {
        var ordered = siblings.Select(n => (Node: n, Name: BaseName(n))).OrderBy(x => x.Node.Seq).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firsts = new HashSet<Node>();
        foreach (var group in ordered.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            used.Add(first.Name);
            firsts.Add(first.Node);
        }

        var result = new List<(Node, string)>(ordered.Count);
        foreach (var (node, name) in ordered)
        {
            if (firsts.Contains(node))
            {
                result.Add((node, name));
                continue;
            }

            var ext = Path.GetExtension(name);
            var stem = name[..^ext.Length];
            var n = 2;
            string candidate;
            do
            {
                candidate = $"{stem} ({n++}){ext}";
            }
            while (!used.Add(candidate));

            result.Add((node, candidate));
        }

        return result;
    }

    /// <summary>Title for a name written by a client (PUT, MKCOL, MOVE): the name without its extension for files.</summary>
    public static string TitleFromName(string name, bool isFile) =>
        isFile && Path.GetExtension(name).Length > 0 ? Path.GetFileNameWithoutExtension(name) : name;

    /// <summary>Office and shell temporary files that the plan keeps out of the library.</summary>
    public static bool IsTransient(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("._", StringComparison.Ordinal) || name.Equals(".DS_Store", StringComparison.Ordinal);
}
