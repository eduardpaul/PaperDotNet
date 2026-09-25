using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;

namespace PaperDotNet.Import.Papermerge;

/// <summary>What to read and how to name the target (PLT-15).</summary>
/// <param name="ConnectionString">Npgsql connection string of the Papermerge database.</param>
/// <param name="MediaRoot">Papermerge's <c>media_root</c> (with the <c>docvers</c> folder); copy S3 buckets to a folder first.</param>
public sealed record PapermergeImportOptions(string ConnectionString, string MediaRoot)
{
    public string WorkspaceName { get; init; } = "Papermerge";

    public string LibraryName { get; init; } = "Documents";

    /// <summary>Read a schema version this converter was not tested with (at your own risk).</summary>
    public bool AllowUnsupportedVersion { get; init; }
}

/// <summary>A Papermerge user the target needs; created without a password (they sign in after a reset).</summary>
public sealed record PapermergeUser(string UserName, string? Email, string? DisplayName, bool WasSuperuser);

/// <summary>The result of a conversion: users to create first, counts and what could not be carried over.</summary>
public sealed record PapermergeConversion(
    string SchemaVersion, IReadOnlyList<PapermergeUser> Users, int Folders, int Documents, int Versions, int Tags, IReadOnlyList<string> Warnings);

public sealed class PapermergeImportException(string message) : Exception(message);

/// <summary>
/// Converts a Papermerge 3.6 database and media folder into a PaperDotNet package (ADR-0029): one workspace with a
/// library; per owner a folder with unique permissions (home, with the inbox as a sub-folder); documents with every
/// file version, page texts and original stamps; tags as terms; document types as content types with their custom fields.
/// </summary>
public static partial class PapermergeConverter
{
    /// <summary>Alembic revisions of papermerge-core 3.6 whose tables this converter reads as they are.</summary>
    public static readonly IReadOnlyList<string> SupportedVersions = ["a07f7fbbcca8", "89be7e6a3444", "c1fa0c57ba3e", "fa71c2c795a9", "bb19aac50bca"];

    public const string TermGroup = "Papermerge";
    public const string TagSet = "Tags";
    public const string DefaultContentType = "Papermerge document";
    private const string TagsField = "tags";

    private static readonly XNamespace Ns = "urn:paperdotnet:template:1";
    private static readonly XNamespace Doc = "urn:paperdotnet:documents:1";

    /// <summary>Reads the database and writes the package (a zip) to <paramref name="output"/>.</summary>
    public static async Task<PapermergeConversion> ConvertAsync(PapermergeImportOptions options, Stream output, CancellationToken ct)
    {
        var (db, dataSource) = PapermergeDbContext.Open(options.ConnectionString);
        await using var _ = dataSource;
        await using var __ = db;
        var version = (await db.AlembicVersion.ToListAsync(ct)).FirstOrDefault()?.VersionNum
            ?? throw new PapermergeImportException("This is not a Papermerge database (no alembic_version).");
        if (!SupportedVersions.Contains(version) && !options.AllowUnsupportedVersion)
        {
            throw new PapermergeImportException(
                $"Papermerge schema version {version} is not supported (supported: {string.Join(", ", SupportedVersions)}, papermerge-core 3.6).");
        }

        var source = await Source.LoadAsync(db, ct);
        var writer = new Writer(options, source, db);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await writer.WriteAsync(zip, ct);
        }

        return new PapermergeConversion(version, writer.Users, writer.FolderCount, writer.DocumentCount, writer.VersionCount, writer.TagCount, writer.Warnings);
    }

    /// <summary>Everything but page texts, read once (texts are read per version while writing).</summary>
    private sealed class Source
    {
        public required List<PmUser> Users { get; init; }
        public required List<PmGroup> Groups { get; init; }
        public required List<PmUserGroup> Memberships { get; init; }
        public required Dictionary<Guid, PmNode> Nodes { get; init; }
        public required Dictionary<Guid, PmDocument> Documents { get; init; }
        public required ILookup<Guid, PmDocumentVersion> Versions { get; init; }
        public required Dictionary<Guid, PmTag> Tags { get; init; }
        public required ILookup<Guid, Guid> NodeTags { get; init; }
        public required Dictionary<Guid, PmCustomField> CustomFields { get; init; }
        public required ILookup<Guid, PmCustomFieldValue> Values { get; init; }
        public required Dictionary<Guid, PmDocumentType> DocumentTypes { get; init; }
        public required ILookup<Guid, PmDocumentTypeField> TypeFields { get; init; }
        public required Dictionary<Guid, (string Type, Guid Id)> Owners { get; init; }
        public required List<PmSpecialFolder> SpecialFolders { get; init; }
        public required ILookup<Guid, PmSharedNode> Shares { get; init; }
        public required Dictionary<Guid, HashSet<string>> RolePermissions { get; init; }

        public static async Task<Source> LoadAsync(PapermergeDbContext db, CancellationToken ct)
        {
            var permissions = await db.Permissions.ToDictionaryAsync(p => p.Id, p => p.Codename, ct);
            var rolePermissions = (await db.RolePermissions.ToListAsync(ct))
                .GroupBy(r => r.RoleId)
                .ToDictionary(g => g.Key, g => g.Select(r => permissions.GetValueOrDefault(r.PermissionId)).OfType<string>().ToHashSet(StringComparer.Ordinal));
            var ownerships = await db.Ownerships.ToListAsync(ct);
            return new Source
            {
                Users = await db.Users.Where(u => u.DeletedAt == null).ToListAsync(ct),
                Groups = await db.Groups.ToListAsync(ct),
                Memberships = await db.UserGroups.ToListAsync(ct),
                Nodes = await db.Nodes.Where(n => n.DeletedAt == null).ToDictionaryAsync(n => n.Id, ct),
                Documents = await db.Documents.ToDictionaryAsync(d => d.NodeId, ct),
                Versions = (await db.Versions.Where(v => v.DeletedAt == null).ToListAsync(ct)).OrderBy(v => v.Number).ToLookup(v => v.DocumentId),
                Tags = await db.Tags.ToDictionaryAsync(t => t.Id, ct),
                NodeTags = (await db.NodeTags.ToListAsync(ct)).ToLookup(t => t.NodeId, t => t.TagId),
                CustomFields = await db.CustomFields.ToDictionaryAsync(f => f.Id, ct),
                Values = (await db.CustomFieldValues.ToListAsync(ct)).ToLookup(v => v.DocumentId),
                DocumentTypes = await db.DocumentTypes.ToDictionaryAsync(t => t.Id, ct),
                TypeFields = (await db.DocumentTypeFields.ToListAsync(ct)).OrderBy(f => f.Position).ToLookup(f => f.DocumentTypeId),
                Owners = ownerships.Where(o => o.ResourceType == "node").ToDictionary(o => o.ResourceId, o => (o.OwnerType, o.OwnerId)),
                SpecialFolders = await db.SpecialFolders.ToListAsync(ct),
                Shares = (await db.SharedNodes.ToListAsync(ct)).ToLookup(s => s.NodeId),
                RolePermissions = rolePermissions,
            };
        }
    }

    private sealed partial class Writer(PapermergeImportOptions options, Source source, PapermergeDbContext db)
    {
        private readonly List<string> _warnings = [];
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, string> _userNames = source.Users.ToDictionary(u => u.Id, u => u.Username);
        private readonly Dictionary<Guid, string> _groupNames = source.Groups.ToDictionary(g => g.Id, g => g.Name);
        private readonly Dictionary<Guid, string> _tagNames = [];
        private readonly Dictionary<Guid, FieldPlan> _fields = [];

        public List<PapermergeUser> Users { get; } = [];

        public List<string> Warnings => _warnings;

        public int FolderCount { get; private set; }

        public int DocumentCount { get; private set; }

        public int VersionCount { get; private set; }

        public int TagCount => _tagNames.Values.Distinct(StringComparer.Ordinal).Count();

        public async Task WriteAsync(ZipArchive zip, CancellationToken ct)
        {
            foreach (var user in source.Users.Where(u => u.IsActive).OrderBy(u => u.Username, StringComparer.Ordinal))
            {
                var name = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
                Users.Add(new PapermergeUser(user.Username, string.IsNullOrWhiteSpace(user.Email) ? null : user.Email, name.Length > 0 ? name : null, user.IsSuperuser));
            }

            PlanTags();
            var contentTypes = PlanContentTypes();
            var items = BuildItems();
            var files = await BuildFilesAsync(zip, ct);

            await WriteEntryAsync(zip, "content/items.json", new JsonObject { ["items"] = items }.ToJsonString(), ct);
            await WriteEntryAsync(zip, "content/files.json", new JsonObject { ["files"] = files }.ToJsonString(), ct);
            var template = BuildTemplate(contentTypes, items.Count, files.Count);
            await WriteEntryAsync(zip, "template.xml", template.ToString(), ct);

            foreach (var type in source.DocumentTypes.Values.Where(t => !string.IsNullOrWhiteSpace(t.PathTemplate)))
            {
                _warnings.Add($"Document type '{type.Name}' has the path template '{type.PathTemplate}'; set up an automation with the item.file action for it.");
            }

            foreach (var user in Users.Where(u => u.WasSuperuser))
            {
                _warnings.Add($"'{user.UserName}' was a Papermerge superuser; assign the Administrator role if needed.");
            }
        }

        // ---- Tags and content types -----------------------------------------------------------

        private void PlanTags()
        {
            foreach (var tag in source.Tags.Values)
            {
                var name = tag.Name.Trim().Replace('/', '-');
                if (name != tag.Name.Trim())
                {
                    _warnings.Add($"Tag '{tag.Name}' is named '{name}' ('/' separates term paths).");
                }

                _tagNames[tag.Id] = name.Length > 255 ? name[..255] : name;
            }
        }

        private List<(string Name, List<FieldPlan> Fields)> PlanContentTypes()
        {
            var result = new List<(string, List<FieldPlan>)> { (DefaultContentType, []) };
            var usedTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultContentType };
            foreach (var type in source.DocumentTypes.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var fields = new List<FieldPlan>();
                var names = new HashSet<string>(StringComparer.Ordinal) { TagsField };
                foreach (var link in source.TypeFields[type.Id])
                {
                    if (!source.CustomFields.TryGetValue(link.CustomFieldId, out var custom))
                    {
                        continue;
                    }

                    var plan = _fields.TryGetValue(custom.Id, out var planned) ? planned : _fields[custom.Id] = PlanField(custom);
                    if (names.Add(plan.Name))
                    {
                        fields.Add(plan);
                    }
                }

                var typeName = usedTypeNames.Add(type.Name) ? type.Name : $"{type.Name} (Papermerge)";
                result.Add((typeName, fields));
                _typeNames[type.Id] = typeName;
            }

            return result;
        }

        private readonly Dictionary<Guid, string> _typeNames = [];

        private FieldPlan PlanField(PmCustomField field)
        {
            var name = FieldName(field.Name);
            var config = ParseJson(field.Config);
            var (type, multiple) = field.TypeHandler switch
            {
                "boolean" => ("boolean", false),
                "date" => ("date", false),
                "datetime" => ("dateTime", false),
                "integer" or "number" => ("number", false),
                "monetary" => ("currency", false),
                "select" => ("choice", false),
                "multiselect" => ("choice", true),
                "text" => ("note", false),
                "url" => ("url", false),
                "email" => ("email", false),
                _ => ("text", false), // short_text, yearmonth and unknown handlers
            };
            if (field.TypeHandler is not ("boolean" or "date" or "datetime" or "integer" or "number" or "monetary" or "select" or "multiselect"
                or "text" or "url" or "email" or "short_text" or "yearmonth"))
            {
                _warnings.Add($"Custom field '{field.Name}' has the unknown type '{field.TypeHandler}' and is imported as text.");
            }

            var choices = new List<string>();
            if (type == "choice")
            {
                foreach (var option in (config?["options"] as JsonArray) ?? [])
                {
                    var value = option is JsonObject o ? (o["value"] ?? o["label"])?.ToString() : option?.ToString();
                    if (!string.IsNullOrWhiteSpace(value) && !choices.Contains(value))
                    {
                        choices.Add(value);
                    }
                }

                // Values in use that the configuration does not list.
                foreach (var value in source.Values.SelectMany(g => g).Where(v => v.FieldId == field.Id).SelectMany(ChoiceValues))
                {
                    if (!choices.Contains(value))
                    {
                        choices.Add(value);
                    }
                }
            }

            var currency = config?["currency"]?.ToString() is { Length: 3 } code ? code.ToUpperInvariant() : "EUR";
            return new FieldPlan(field.Id, name, field.Name, type, multiple, choices, type == "currency" ? currency : null, field.TypeHandler);
        }

        private static string FieldName(string displayName)
        {
            var words = WordPattern().Matches(displayName.Normalize(NormalizationForm.FormD)).Select(m => m.Value).ToList();
            var builder = new StringBuilder();
            foreach (var word in words)
            {
                builder.Append(builder.Length == 0 ? word.ToLowerInvariant() : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant());
            }

            var name = builder.ToString();
            if (name.Length == 0 || !char.IsAsciiLetterLower(name[0]))
            {
                name = "field" + name;
            }

            name = name.Length > 60 ? name[..60] : name;
            return name is "title" or "tags" or "id" ? name + "Value" : name;
        }

        // ---- Items (folders and documents) ----------------------------------------------------

        private JsonArray BuildItems()
        {
            var items = new JsonArray();
            var home = source.SpecialFolders.Where(f => f.FolderType == "home").ToDictionary(f => f.FolderId);
            var inbox = source.SpecialFolders.Where(f => f.FolderType == "inbox").ToDictionary(f => f.FolderId);
            var ownerFolders = new Dictionary<(string, Guid), string>();

            string OwnerFolder((string Type, Guid Id) owner)
            {
                if (ownerFolders.TryGetValue(owner, out var key))
                {
                    return key;
                }

                var homeFolder = source.SpecialFolders.FirstOrDefault(f => f.FolderType == "home" && f.OwnerType == owner.Type && f.OwnerId == owner.Id);
                key = homeFolder is not null && source.Nodes.ContainsKey(homeFolder.FolderId) ? Key(homeFolder.FolderId) : $"owner-{owner.Type}-{owner.Id:N}";
                ownerFolders[owner] = key;
                if (homeFolder is null || !source.Nodes.TryGetValue(homeFolder.FolderId, out var node))
                {
                    items.Add(Folder(key, OwnerLabel(owner), null, null, Grants(owner, [])));
                    FolderCount++;
                }
                else
                {
                    items.Add(Folder(key, OwnerLabel(owner), null, node, Grants(owner, [.. source.Shares[node.Id]])));
                    FolderCount++;
                }

                return key;
            }

            // Parents before children: walk from the roots.
            var children = source.Nodes.Values.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!.Value);
            var queue = new Queue<(PmNode Node, string? ParentKey, (string, Guid) Owner, List<PmSharedNode> Shares)>();
            foreach (var root in source.Nodes.Values.Where(n => n.ParentId is null || !source.Nodes.ContainsKey(n.ParentId.Value)).OrderBy(n => n.CreatedAt))
            {
                var owner = OwnerOf(root);
                if (home.ContainsKey(root.Id))
                {
                    var key = OwnerFolder(owner);
                    foreach (var child in children[root.Id].OrderBy(n => n.CreatedAt))
                    {
                        queue.Enqueue((child, key, owner, [.. source.Shares[root.Id]]));
                    }

                    continue;
                }

                var parent = OwnerFolder(owner);
                queue.Enqueue((root, parent, owner, []));
            }

            while (queue.Count > 0)
            {
                var (node, parentKey, owner, inherited) = queue.Dequeue();
                var shares = source.Shares[node.Id].ToList();
                if (node.Ctype == "folder")
                {
                    var title = inbox.ContainsKey(node.Id) ? "Inbox" : node.Title;
                    items.Add(Folder(Key(node.Id), title, parentKey, node, shares.Count > 0 ? Grants(owner, [.. inherited, .. shares]) : null));
                    FolderCount++;
                    foreach (var child in children[node.Id].OrderBy(n => n.CreatedAt))
                    {
                        queue.Enqueue((child, Key(node.Id), owner, [.. inherited, .. shares]));
                    }
                }
                else if (node.Ctype == "document")
                {
                    items.Add(Document(node, parentKey, shares.Count > 0 ? Grants(owner, [.. inherited, .. shares]) : null));
                    DocumentCount++;
                }
            }

            return items;
        }

        private (string Type, Guid Id) OwnerOf(PmNode node)
        {
            for (var current = node; current is not null; current = current.ParentId is { } parent ? source.Nodes.GetValueOrDefault(parent) : null)
            {
                if (source.Owners.TryGetValue(current.Id, out var owner))
                {
                    return owner;
                }
            }

            return ("user", node.CreatedBy ?? Guid.Empty);
        }

        private string OwnerLabel((string Type, Guid Id) owner) =>
            owner.Type == "group"
                ? $"{_groupNames.GetValueOrDefault(owner.Id) ?? owner.Id.ToString("N")} (group)"
                : _userNames.GetValueOrDefault(owner.Id) ?? "Unknown owner";

        /// <summary>The owner's grant (Manage for a user, Contribute for a group) plus shares by role.</summary>
        private JsonArray Grants((string Type, Guid Id) owner, IEnumerable<PmSharedNode> shares)
        {
            var grants = new Dictionary<string, (string Kind, string Name, int Level)>(StringComparer.Ordinal);
            void Add(string kind, string? name, int level)
            {
                if (name is not null && (!grants.TryGetValue($"{kind}:{name}", out var existing) || existing.Level < level))
                {
                    grants[$"{kind}:{name}"] = (kind, name, level);
                }
            }

            Add(owner.Type == "group" ? "group" : "user", owner.Type == "group" ? _groupNames.GetValueOrDefault(owner.Id) : _userNames.GetValueOrDefault(owner.Id), owner.Type == "group" ? 1 : 2);
            foreach (var share in shares)
            {
                var codes = source.RolePermissions.GetValueOrDefault(share.RoleId) ?? [];
                var level = codes.Overlaps(["node.update", "node.create", "node.delete", "node.move"]) ? 1 : 0;
                Add(share.GroupId is { } g ? "group" : "user", share.GroupId is { } group ? _groupNames.GetValueOrDefault(group) : share.UserId is { } user ? _userNames.GetValueOrDefault(user) : null, level);
            }

            return new JsonArray([.. grants.Values.Select(g => new JsonObject
            {
                [g.Kind] = g.Name,
                ["level"] = g.Level switch { 2 => "Manage", 1 => "Contribute", _ => "Read" },
            })]);
        }

        private JsonObject Folder(string key, string title, string? parentKey, PmNode? node, JsonArray? permissions)
        {
            var entry = new JsonObject { ["key"] = key, ["title"] = title, ["contentType"] = DefaultContentType, ["folder"] = true };
            if (parentKey is not null)
            {
                entry["parent"] = parentKey;
            }

            if (node is not null)
            {
                Stamp(entry, node);
                if (Tags(node.Id) is { Count: > 0 } tags)
                {
                    entry["fields"] = new JsonObject { [TagsField] = tags };
                }
            }

            if (permissions is not null)
            {
                entry["permissions"] = permissions;
            }

            return entry;
        }

        private JsonObject Document(PmNode node, string? parentKey, JsonArray? permissions)
        {
            var document = source.Documents.GetValueOrDefault(node.Id);
            var type = document?.DocumentTypeId is { } typeId && _typeNames.TryGetValue(typeId, out var typeName) ? typeName : DefaultContentType;
            var fields = new JsonObject();
            if (Tags(node.Id) is { Count: > 0 } tags)
            {
                fields[TagsField] = tags;
            }

            if (document?.DocumentTypeId is { } documentType)
            {
                var allowed = source.TypeFields[documentType].Select(f => f.CustomFieldId).ToHashSet();
                foreach (var value in source.Values[node.Id].Where(v => allowed.Contains(v.FieldId)))
                {
                    if (_fields.TryGetValue(value.FieldId, out var plan) && Value(plan, value) is { } converted)
                    {
                        fields[plan.Name] = converted;
                    }
                }
            }

            var entry = new JsonObject { ["key"] = Key(node.Id), ["title"] = node.Title, ["contentType"] = type, ["fields"] = fields };
            if (parentKey is not null)
            {
                entry["parent"] = parentKey;
            }

            Stamp(entry, node);
            if (permissions is not null)
            {
                entry["permissions"] = permissions;
            }

            return entry;
        }

        private JsonArray? Tags(Guid nodeId)
        {
            var paths = source.NodeTags[nodeId].Select(t => _tagNames.GetValueOrDefault(t)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            return paths.Count == 0 ? null : new JsonArray([.. paths.Select(p => JsonValue.Create($"{TermGroup}/{TagSet}/{p}"))]);
        }

        private void Stamp(JsonObject entry, PmNode node)
        {
            entry["created"] = node.CreatedAt;
            entry["createdBy"] = node.CreatedBy is { } by ? _userNames.GetValueOrDefault(by) : null;
            entry["modified"] = node.UpdatedAt;
            entry["modifiedBy"] = node.UpdatedBy is { } editor ? _userNames.GetValueOrDefault(editor) : null;
        }

        private static JsonNode? Value(FieldPlan plan, PmCustomFieldValue value) => plan.Type switch
        {
            "boolean" => value.ValueBoolean is { } b ? JsonValue.Create(b) : Raw(value) is JsonValue v && v.TryGetValue<bool>(out var parsed) ? JsonValue.Create(parsed) : null,
            "date" => value.ValueDate is { } d ? JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : Raw(value)?.DeepClone(),
            "dateTime" => value.ValueDatetime is { } t ? JsonValue.Create(t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) : Raw(value)?.DeepClone(),
            "number" or "currency" => value.ValueNumeric is { } n ? JsonValue.Create(n) : Raw(value) is JsonValue v && v.TryGetValue<decimal>(out var number) ? JsonValue.Create(number) : null,
            "choice" when plan.Multiple => ChoiceValues(value).ToList() is { Count: > 0 } list ? new JsonArray([.. list.Select(c => JsonValue.Create(c))]) : null,
            _ => (value.ValueText ?? Raw(value)?.ToString()) is { Length: > 0 } text ? JsonValue.Create(text) : null,
        };

        private static IEnumerable<string> ChoiceValues(PmCustomFieldValue value)
        {
            if (value.ValueText is { Length: > 0 } text && Raw(value) is not JsonArray)
            {
                return [text];
            }

            return Raw(value) switch
            {
                JsonArray array => array.Select(v => v?.ToString()).OfType<string>().Where(v => v.Length > 0),
                JsonValue single when single.ToString() is { Length: > 0 } s => [s],
                _ => [],
            };
        }

        /// <summary>The JSON <c>value</c> column: either the value itself or an object with a <c>value</c> property.</summary>
        private static JsonNode? Raw(PmCustomFieldValue value) =>
            ParseJson(value.Value) is JsonObject o && o.ContainsKey("value") ? o["value"] : ParseJson(value.Value);

        private static JsonNode? ParseJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ---- Files ------------------------------------------------------------------------------

        private async Task<JsonArray> BuildFilesAsync(ZipArchive zip, CancellationToken ct)
        {
            var entries = new JsonArray();
            foreach (var document in source.Documents.Values.Where(d => source.Nodes.ContainsKey(d.NodeId)))
            {
                var versions = new JsonArray();
                foreach (var version in source.Versions[document.NodeId])
                {
                    var path = Path.Combine(options.MediaRoot, "docvers", version.Id.ToString()[..2], version.Id.ToString()[2..4], version.Id.ToString(), version.FileName ?? string.Empty);
                    if (version.FileName is null || !File.Exists(path))
                    {
                        _warnings.Add($"'{source.Nodes[document.NodeId].Title}' version {version.Number}: the file is missing ({path}) and was left out.");
                        continue;
                    }

                    var texts = await db.Pages.Where(p => p.DocumentVersionId == version.Id).OrderBy(p => p.Number).Select(p => new { p.Number, p.Text }).ToListAsync(ct);
                    string? pages = null;
                    if (texts.Any(t => !string.IsNullOrWhiteSpace(t.Text)))
                    {
                        var array = new JsonArray();
                        foreach (var text in texts)
                        {
                            while (array.Count < text.Number - 1)
                            {
                                array.Add(string.Empty);
                            }

                            array.Add(text.Text ?? string.Empty);
                        }

                        pages = await AddFileAsync(zip, Encoding.UTF8.GetBytes(array.ToJsonString()), ct);
                    }

                    var language = version.Lang is { } lang && LanguagePattern().IsMatch(lang) ? lang : null;
                    versions.Add(new JsonObject
                    {
                        ["file"] = await AddFileAsync(zip, path, ct),
                        ["name"] = version.FileName,
                        ["source"] = version.CreationReason is { Length: > 0 and <= 20 } reason ? reason : "import",
                        ["created"] = version.CreatedAt,
                        ["createdBy"] = version.CreatedBy is { } by ? _userNames.GetValueOrDefault(by) : null,
                        ["languages"] = language,
                        ["textLanguage"] = language,
                        ["pages"] = pages,
                    });
                    VersionCount++;
                }

                if (versions.Count == 0)
                {
                    continue;
                }

                var current = (JsonObject)versions[^1]!;
                entries.Add(new JsonObject
                {
                    ["key"] = Key(document.NodeId),
                    ["file"] = current["file"]!.DeepClone(),
                    ["name"] = current["name"]!.DeepClone(),
                    ["versions"] = versions,
                });
            }

            return entries;
        }

        private async Task<string> AddFileAsync(ZipArchive zip, string path, CancellationToken ct)
        {
            string hash;
            await using (var file = File.OpenRead(path))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            }

            var name = $"files/{hash}";
            if (_files.Add(name))
            {
                await using var file = File.OpenRead(path);
                await using var entry = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(ct);
                await file.CopyToAsync(entry, ct);
            }

            return name;
        }

        private async Task<string> AddFileAsync(ZipArchive zip, byte[] content, CancellationToken ct)
        {
            var name = $"files/{Convert.ToHexStringLower(SHA256.HashData(content))}";
            if (_files.Add(name))
            {
                await using var entry = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(ct);
                await entry.WriteAsync(content, ct);
            }

            return name;
        }

        private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
        {
            await using var entry = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(ct);
            await entry.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
        }

        // ---- Template -----------------------------------------------------------------------------

        private XDocument BuildTemplate(List<(string Name, List<FieldPlan> Fields)> contentTypes, int itemCount, int fileCount)
        {
            var activeUsers = Users.Select(u => u.UserName).ToHashSet(StringComparer.Ordinal);
            var groups = new XElement(Ns + "Groups", source.Groups.OrderBy(g => g.Name, StringComparer.Ordinal).Select(g => new XElement(Ns + "Group",
                new XAttribute("Name", g.Name),
                source.Memberships.Where(m => m.GroupId == g.Id).Select(m => _userNames.GetValueOrDefault(m.UserId)).OfType<string>()
                    .Where(activeUsers.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                    .Select(u => new XElement(Ns + "Member", new XAttribute("User", u))))));
            var tags = source.Tags.Values
                .GroupBy(t => _tagNames[t.Id], StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var tag = g.First();
                    var term = new XElement(Ns + "Term", new XAttribute("Name", g.Key));
                    if (!string.IsNullOrWhiteSpace(tag.Description))
                    {
                        term.Add(new XAttribute("Description", tag.Description));
                    }

                    if (tag.BgColor is { } color && ColorPattern().IsMatch(color))
                    {
                        term.Add(new XAttribute("Color", color));
                    }

                    return term;
                });
            var termGroups = new XElement(Ns + "TermGroups", new XElement(Ns + "TermGroup", new XAttribute("Name", TermGroup),
                new XElement(Ns + "TermSet", new XAttribute("Name", TagSet), new XAttribute("Open", true), tags)));
            var types = new XElement(Ns + "ContentTypes", contentTypes.Select(t => new XElement(Ns + "ContentType",
                new XAttribute("Name", t.Name),
                new XAttribute("Description", "Imported from Papermerge."),
                new XElement(Ns + "Field", new XAttribute("Name", TagsField), new XAttribute("DisplayName", "Tags"), new XAttribute("Type", "managedMetadata"),
                    new XAttribute("AllowMultiple", true), new XAttribute("TermSet", $"{TermGroup}/{TagSet}")),
                t.Fields.Select(Field))));
            var list = new XElement(Ns + "List",
                new XAttribute("Name", options.LibraryName),
                new XAttribute("Kind", "Library"),
                new XAttribute("Versioning", "Major"),
                new XElement(Ns + "ContentTypes", contentTypes.Select(t => new XElement(Ns + "ContentTypeRef", new XAttribute("Name", t.Name)))),
                new XElement(Ns + "Items", new XAttribute("File", "content/items.json"), new XAttribute("Count", itemCount)));
            if (fileCount > 0)
            {
                list.Add(new XElement(Doc + "Files", new XAttribute(XNamespace.Xmlns + "doc", Doc), new XAttribute("File", "content/files.json"),
                    new XAttribute("Count", fileCount)));
            }

            var workspace = new XElement(Ns + "Workspace",
                new XAttribute("Name", options.WorkspaceName),
                new XAttribute("Description", "Imported from Papermerge."),
                new XElement(Ns + "Members", Users.Select(u => new XElement(Ns + "Member", new XAttribute("User", u.UserName), new XAttribute("Role", "Visitor")))),
                new XElement(Ns + "Lists", list));
            return new XDocument(new XElement(Ns + "Template",
                new XAttribute("SchemaVersion", "1.0"),
                new XAttribute("Scope", "Workspace"),
                new XAttribute("Name", "Papermerge import"),
                groups, termGroups, types, new XElement(Ns + "Workspaces", workspace)));
        }

        private static XElement Field(FieldPlan field)
        {
            var element = new XElement(Ns + "Field", new XAttribute("Name", field.Name), new XAttribute("DisplayName", field.DisplayName), new XAttribute("Type", field.Type));
            if (field.Multiple)
            {
                element.Add(new XAttribute("AllowMultiple", true));
            }

            if (field.CurrencyCode is { } currency)
            {
                element.Add(new XAttribute("CurrencyCode", currency));
            }

            if (field.Handler == "yearmonth")
            {
                element.Add(new XAttribute("Description", "Year and month (yyyy-MM)."));
            }

            element.Add(field.Choices.Select(c => new XElement(Ns + "Choice", c)));
            return element;
        }

        private static string Key(Guid id) => id.ToString("N");

        [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
        private static partial Regex WordPattern();

        [GeneratedRegex("^[a-z][a-z_]{1,30}(\\+[a-z][a-z_]{1,30}){0,5}$")]
        private static partial Regex LanguagePattern();

        [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
        private static partial Regex ColorPattern();
    }

    private sealed record FieldPlan(
        Guid Id, string Name, string DisplayName, string Type, bool Multiple, List<string> Choices, string? CurrencyCode, string Handler);
}
