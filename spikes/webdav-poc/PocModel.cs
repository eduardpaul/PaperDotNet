using System.Security.Cryptography;

namespace WebDavPoc;

public enum NodeKind { Root, Workspace, Library, List, Folder, File }

/// <summary>
/// An in-memory stand-in for workspaces, lists, folders and documents. It mirrors what the Dav module would read
/// through IWorkspaceAccess / IListItemStore / IDocumentFileStore: ids, titles, an ordering key, versions.
/// </summary>
public sealed class Node
{
    private static long sequence;

    public Guid Id { get; } = Guid.CreateVersion7();

    /// <summary>Creation order (stands in for UUIDv7 order in the database).</summary>
    public long Seq { get; } = Interlocked.Increment(ref sequence);

    public required NodeKind Kind { get; init; }
    public required string Title { get; set; }
    public Node? Parent { get; set; }
    public List<Node> Children { get; } = [];

    /// <summary>The stored file name of the current version (its extension is shown in WebDAV).</summary>
    public string? FileName { get; set; }
    public byte[]? Content { get; set; }
    public string MediaType { get; set; } = "application/octet-stream";
    public uint Version { get; set; } = 1;
    public int FileVersions { get; set; }
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public string Sha256 { get; private set; } = "";

    /// <summary>Caller may write (Contribute).</summary>
    public bool Writable { get; set; } = true;

    public bool IsCollection => Kind is NodeKind.Root or NodeKind.Workspace or NodeKind.Library or NodeKind.Folder;

    public void SetContent(byte[] content, bool newVersion)
    {
        Content = content;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        if (newVersion)
        {
            FileVersions++;
        }

        Version++;
        Updated = DateTimeOffset.UtcNow;
    }

    public Node Add(Node child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    public Node? Library => Kind == NodeKind.Library ? this : Parent?.Library;
}

public sealed class PocModel
{
    public Node Root { get; } = new() { Kind = NodeKind.Root, Title = "" };

    /// <summary>Items deleted through WebDAV (the recycle bin).</summary>
    public List<Node> RecycleBin { get; } = [];

    public long MaxFileSize { get; set; } = 5 * 1024 * 1024;

    /// <summary>Files renamed to a temporary name (Office safe-save step 1), by folder and former name.</summary>
    public Dictionary<(Node Folder, string Name), (Node Node, DateTimeOffset At)> RenamedAway { get; } = [];

    public object Gate { get; } = new();

    public PocModel()
    {
        var home = Root.Add(new Node { Kind = NodeKind.Workspace, Title = "Home" });
        var documents = home.Add(new Node { Kind = NodeKind.Library, Title = "Documents" });
        home.Add(new Node { Kind = NodeKind.Library, Title = "Inbox" });
        File(documents, "Welcome", "welcome.pdf", "application/pdf", PdfBytes("Welcome"));

        var projects = Root.Add(new Node { Kind = NodeKind.Workspace, Title = "Projects" });
        projects.Add(new Node { Kind = NodeKind.List, Title = "Tasks" }); // not a library: hidden
        var contracts = projects.Add(new Node { Kind = NodeKind.Library, Title = "Contracts" });

        // Duplicate titles, case variants, characters Windows does not allow, reserved device names.
        File(contracts, "Invoice", "invoice-a.pdf", "application/pdf", PdfBytes("Invoice A"));
        File(contracts, "Invoice", "invoice-b.pdf", "application/pdf", PdfBytes("Invoice B"));
        File(contracts, "invoice", "invoice-c.pdf", "application/pdf", PdfBytes("Invoice C"));
        File(contracts, "Report: Q1/2025?", "report.pdf", "application/pdf", PdfBytes("Report"));
        File(contracts, "CON", "con.pdf", "application/pdf", PdfBytes("Con"));
        File(contracts, "Scan without extension", "scan", "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        File(contracts, "Trailing dots...", "dots.pdf", "application/pdf", PdfBytes("Dots"));
        contracts.Add(new Node { Kind = NodeKind.File, Title = "Item without a file" }); // hidden
        var readOnly = File(contracts, "Read only", "ro.pdf", "application/pdf", PdfBytes("Read only"));
        readOnly.Writable = false;

        var big = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(big);
        File(contracts, "Large", "large.bin", "application/octet-stream", big);

        var special = contracts.Add(new Node { Kind = NodeKind.Folder, Title = "Ä Umlaut & #hash 100% [x]" });
        File(special, "Inner", "inner.pdf", "application/pdf", PdfBytes("Inner"));

        var y2025 = contracts.Add(new Node { Kind = NodeKind.Folder, Title = "2025" });
        File(y2025, "Contract A", "a.pdf", "application/pdf", PdfBytes("A"));

        var bigFolder = contracts.Add(new Node { Kind = NodeKind.Folder, Title = "Big" });
        for (var i = 0; i < 5000; i++)
        {
            File(bigFolder, $"Document {i:D5}", $"doc{i}.pdf", "application/pdf", PdfBytes($"Doc {i}"));
        }

        projects.Add(new Node { Kind = NodeKind.Library, Title = "Archive" });
    }

    private static Node File(Node parent, string title, string fileName, string mediaType, byte[] content)
    {
        var node = parent.Add(new Node { Kind = NodeKind.File, Title = title, FileName = fileName, MediaType = mediaType });
        node.SetContent(content, newVersion: true);
        node.Version = 1;
        return node;
    }

    public static byte[] PdfBytes(string text) => System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4\n% {text}\n%%EOF\n");
}
