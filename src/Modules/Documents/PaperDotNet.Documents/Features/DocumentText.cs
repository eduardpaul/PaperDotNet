using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>The trigger documents raise (ADR-0038).</summary>
public static class DocumentTriggers
{
    /// <summary>A file was added to a library: a new document or a new version (data: version, mediaType, fileName, newDocument).</summary>
    public const string Added = "document.added";
}

/// <summary>
/// Announces files to workflows (ADR-0038): an upload, a new version or an imported current version only stores the file
/// and raises <c>document.added</c>. Text, thumbnails, page images and OCR are the library's workflows on it.
/// </summary>
internal sealed class DocumentEvents(IWorkflowTriggers triggers)
{
    public Task AddedAsync(ChangeActor actor, FileVersion version, bool newDocument, CancellationToken ct) =>
        triggers.RaiseAsync(actor, DocumentTriggers.Added, version.WorkspaceId, new WorkflowItem(version.WorkspaceId, version.ListId, version.ItemId),
            new JsonObject
            {
                ["version"] = version.Number,
                ["mediaType"] = version.MediaType,
                ["fileName"] = version.FileName,
                ["newDocument"] = newDocument,
            },
            AddedEventId(version.Id), ct);

    /// <summary>The event id of <c>document.added</c> for a version: announcing it again starts nothing twice.</summary>
    internal static Guid AddedEventId(Guid versionId)
    {
        Span<byte> input = stackalloc byte[17];
        versionId.TryWriteBytes(input);
        input[16] = (byte)'a';
        var bytes = SHA256.HashData(input)[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80); // Version 8 (name-based, custom).
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant.
        return new Guid(bytes);
    }
}

/// <summary>Whether a file has text, the languages to read it in, and its page texts.</summary>
internal static partial class DocumentText
{
    /// <summary>Below this many characters per page a PDF counts as a scan.</summary>
    public const int MinTextPerPage = 20;

    private static readonly Dictionary<string, string> SearchLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dan"] = "danish",
        ["nld"] = "dutch",
        ["eng"] = "english",
        ["fin"] = "finnish",
        ["fra"] = "french",
        ["deu"] = "german",
        ["hun"] = "hungarian",
        ["ita"] = "italian",
        ["nor"] = "norwegian",
        ["por"] = "portuguese",
        ["ron"] = "romanian",
        ["rus"] = "russian",
        ["spa"] = "spanish",
        ["swe"] = "swedish",
        ["tur"] = "turkish",
    };

    /// <summary>Whether page texts are enough to count as text (not a scan).</summary>
    public static bool Enough(IReadOnlyCollection<string> pages) =>
        pages.Count > 0 && pages.Sum(p => p.Trim().Length) >= MinTextPerPage * pages.Count;

    /// <summary>The saved page texts of a stored file, in order.</summary>
    public static async Task<List<string>> PagesAsync(DocumentsDbContext db, Guid tenantId, Guid storedFileId, CancellationToken ct) =>
        [.. (await DocumentQueries.PagesAsync(db, tenantId, storedFileId, ct)).Select(p => p.Text)];

    /// <summary>Whether the version has text: an OCR result, or enough saved page texts.</summary>
    public static async Task<bool> HasTextAsync(DocumentsDbContext db, FileVersion version, CancellationToken ct) =>
        version.Source == "ocr" || Enough(await PagesAsync(db, version.TenantId, version.StoredFileId, ct));

    /// <summary>Stores page texts once per content (identical files share them).</summary>
    public static async Task SavePagesAsync(DocumentsDbContext db, Guid tenantId, Guid storedFileId, IReadOnlyList<string> pages, CancellationToken ct)
    {
        if (pages.Count == 0 || pages.All(string.IsNullOrWhiteSpace) || await DocumentQueries.HasPagesAsync(db, tenantId, storedFileId, ct))
        {
            return;
        }

        db.Pages.AddRange(pages.Select((text, i) => new StoredFilePage { TenantId = tenantId, StoredFileId = storedFileId, PageNumber = i + 1, Text = text.Trim() }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>DOC-17: the request, the file, the library, then the uploader's (or the organization's) document languages.</summary>
    public static async Task<string> LanguagesAsync(
        DocumentsDbContext db, IUserPreferences preferences, FileVersion version, string? requested, CancellationToken ct)
    {
        if ((requested ?? version.Languages) is { } chosen)
        {
            return chosen;
        }

        if ((await DocumentQueries.SettingsAsync(db, version.TenantId, version.ListId, ct))?.OcrLanguages is { } library)
        {
            return library;
        }

        return version.CreatedBy is { } uploader
            ? (await preferences.GetAsync(version.TenantId, uploader, ct)).DocumentLanguages
            : (await preferences.GetDefaultsAsync(version.TenantId, ct)).DocumentLanguages;
    }

    public static string FirstLanguage(string languages) => languages.Split('+')[0];

    /// <summary>The search language (stemming) of a Tesseract language code (ISO 639-2), or null.</summary>
    public static string? SearchLanguage(string? code) => code is null ? null : SearchLanguages.GetValueOrDefault(code);

    /// <summary>Tesseract language list: codes of letters and underscores joined with <c>+</c>.</summary>
    public static bool IsValidLanguageList(string value) => LanguageList().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z_]{1,30}(\\+[a-z][a-z_]{1,30}){0,5}$")]
    private static partial Regex LanguageList();
}

/// <summary>Adds the text of an item's current file to its search document, page by page, with its language (SRC-05, SRC-09).</summary>
internal sealed class DocumentSearchContent(DocumentsDbContext db) : IItemSearchContributor
{
    public async Task<IReadOnlyDictionary<Guid, ItemSearchContent>> GetContentAsync(Guid tenantId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, ItemSearchContent>();
        foreach (var itemId in itemIds)
        {
            if (await DocumentQueries.CurrentAsync(db, tenantId, itemId, cancellationToken) is not { } version)
            {
                continue;
            }

            var pages = await DocumentQueries.PagesAsync(db, tenantId, version.StoredFileId, cancellationToken);
            if (pages.Count == 0)
            {
                continue;
            }

            result[itemId] = new ItemSearchContent(string.Join('\n', pages.Select(p => p.Text)), DocumentText.SearchLanguage(version.TextLanguage))
            {
                Pages = PageTexts(pages),
            };
        }

        return result;
    }

    /// <summary>Texts indexed by page number (missing pages become empty).</summary>
    private static List<string> PageTexts(List<StoredFilePage> pages)
    {
        var texts = new List<string>();
        foreach (var page in pages)
        {
            while (texts.Count < page.PageNumber - 1)
            {
                texts.Add(string.Empty);
            }

            texts.Add(page.Text);
        }

        return texts;
    }
}
