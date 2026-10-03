using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Search.Zvec.Native;

namespace PaperDotNet.Search.Zvec;

/// <summary>
/// The columns of a tenant's collection (ADR-0044). Every column but the numeric field columns is created with the
/// collection, because zvec adds only numeric columns later. A document has a head row (<c>kind = 'doc'</c>, key = its
/// id) holding its text for keyword search, and one row per passage (<c>kind = 'passage'</c>) with its text, page and
/// vector; every row carries the metadata, so one filter covers both. Each full-text column costs about 40 MB of
/// memory per open collection in zvec (its own RocksDB), so there are few: <see cref="Content"/>, its stemmed copies
/// for the configured languages, <see cref="Heading"/> and, optionally, <see cref="Prefix"/>.
/// </summary>
internal static class ZvecLayout
{
    public const string Kind = "kind";
    public const string DocumentKind = "doc";
    public const string PassageKind = "passage";

    public const string DocumentId = "doc_id";
    public const string SourceType = "source_type";
    public const string WorkspaceId = "workspace_id";
    public const string ContainerId = "container_id";
    public const string ContentTypeId = "content_type_id";
    public const string ScopeId = "scope_id";
    public const string TermIds = "term_ids";
    public const string CreatedBy = "created_by";

    /// <summary>UTC ticks.</summary>
    public const string UpdatedAt = "updated_at";

    /// <summary>Keys of the fields the document has (<see cref="FieldKey"/>).</summary>
    public const string FieldNames = "field_names";

    /// <summary>Text-kind and boolean field values as tokens (<see cref="Token"/>).</summary>
    public const string FieldTokens = "field_tokens";

    /// <summary>The title as stored (not indexed; <see cref="Heading"/> is).</summary>
    public const string Title = "title";
    public const string Language = "language";

    /// <summary>
    /// What keyword search matches: on head rows the title, keywords, body and pages; on passage rows the passage
    /// (its snippet and page).
    /// </summary>
    public const string Content = "content";

    /// <summary>
    /// The constant <see cref="AllToken"/> on every row (so a search with filters only can run as a query), then the
    /// title and keywords of head rows (their matches rank higher).
    /// </summary>
    public const string Heading = "heading";

    /// <summary>A word no one searches for: present in every row's <see cref="Heading"/>.</summary>
    public const string AllToken = "pdnallrows";

    /// <summary>Trigrams of <see cref="Content"/>, for <c>word*</c> terms (the full-text grammar has no prefix terms).</summary>
    public const string Prefix = "prefix";

    /// <summary>The passage text as stored (not indexed; <see cref="Content"/> is).</summary>
    public const string Text = "text";
    public const string Ordinal = "ordinal";
    public const string Page = "page";
    public const string ContentHash = "content_hash";
    public const string EmbeddingModel = "emb_model";
    public const string Embedding = "embedding";

    /// <summary>Written into array columns that would otherwise be empty, so <c>NOT CONTAIN_ANY</c> never meets a null.</summary>
    public const string Sentinel = "-";

    /// <summary>The stemmed copy of <see cref="Content"/> in a language (Snowball names, as <c>FullTextLanguages</c>).</summary>
    public static string ContentIn(string language) => $"content_{language}";

    public static readonly IReadOnlyList<string> Languages =
    [
        "danish", "dutch", "english", "finnish", "french", "german", "hungarian", "italian",
        "norwegian", "portuguese", "romanian", "russian", "spanish", "swedish", "turkish",
    ];

    public static IEnumerable<ZvecColumn> Columns(int? dimensions, IReadOnlyCollection<string> languages, bool prefix)
    {
        string[] plain = ["lowercase", "ascii_folding"];
        foreach (var id in new[] { DocumentId, Kind, SourceType, WorkspaceId, ContainerId, ContentTypeId, ScopeId, CreatedBy, ContentHash, EmbeddingModel })
        {
            yield return new ZvecScalar(id, ZvecNative.TypeString, Index: true);
        }

        yield return new ZvecScalar(TermIds, ZvecNative.TypeArrayString, Index: true);
        yield return new ZvecScalar(FieldNames, ZvecNative.TypeArrayString, Index: true);
        yield return new ZvecScalar(FieldTokens, ZvecNative.TypeArrayString, Index: true);
        yield return new ZvecScalar(UpdatedAt, ZvecNative.TypeInt64, Index: true, Range: true);
        yield return new ZvecScalar(Ordinal, ZvecNative.TypeInt32, Index: true, Range: true);
        yield return new ZvecScalar(Page, ZvecNative.TypeInt32, Index: false);
        yield return new ZvecScalar(Language, ZvecNative.TypeString, Index: false);
        yield return new ZvecScalar(Title, ZvecNative.TypeString, Index: false);
        yield return new ZvecScalar(Text, ZvecNative.TypeString, Index: false);
        yield return new ZvecText(Content, "standard", plain);
        yield return new ZvecText(Heading, "standard", plain);
        foreach (var language in languages.Where(Languages.Contains))
        {
            yield return new ZvecText(ContentIn(language), "standard", [.. plain, "stemmer"], $$"""{"stemmer_lang":"{{language}}"}""");
        }

        if (prefix)
        {
            yield return new ZvecText(Prefix, "ngram", ["lowercase", "ascii_folding"], """{"ngram_min":3,"ngram_max":3}""");
        }

        if (dimensions is { } d)
        {
            yield return new ZvecVector(Embedding, d);
        }
    }

    /// <summary>A stable key for a field name and kind: hex, so it is safe in filters and column names.</summary>
    public static string FieldKey(string name, SearchFieldKind kind) => Hash($"{kind}:{name}", 16);

    /// <summary>The DOUBLE column of a number, date or time field.</summary>
    public static string NumberColumn(string key) => $"n_{key}";

    /// <summary>
    /// The token of a text-kind or boolean value: <c>{key}:{base64url}</c>. User text never appears raw in a filter
    /// (zvec cannot match a literal backslash).
    /// </summary>
    public static string Token(string key, SearchFieldKind kind, SearchValue value) => kind == SearchFieldKind.Boolean
        ? $"{key}:{(value.Number is > 0 ? "1" : "0")}"
        : $"{key}:{Base64Url(value.Text ?? string.Empty)}";

    /// <summary>Ids as stored: 32 hex digits.</summary>
    public static string Id(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    public static string Hash(string value, int length) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..length];

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
