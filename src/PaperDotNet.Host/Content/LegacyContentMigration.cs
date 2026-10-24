using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Content;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Data;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Persistence;
using PaperDotNet.Taxonomy.Data;
using PaperDotNet.Workspaces.Data;
using PaperDotNet.Workspaces.Contracts;

namespace PaperDotNet.Host.Content;

public sealed record ContentMigrationPreview(string WorkspaceId, string Name, string SourceHash, int Collections, int Items,
    int HistoryEntries, int Files, bool AlreadyImported, IReadOnlyList<string> Blockers);
public sealed record ContentMigrationRequest(string SourceHash);

/// <summary>Preserves identifiers, bytes, metadata history and permissions. Never edits the source database.</summary>
internal sealed partial class LegacyContentMigration(ListsDbContext lists, WorkspacesDbContext workspaces, DocumentsDbContext documents,
    IdentityDbContext identity, TaxonomyDbContext taxonomy, IContentMigrationLock migrationLock, PaperDotNet.Abstractions.ITenantContext tenant)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string SystemGroup = "00000000-0000-4000-8000-000000000001";
    private const string SystemKeywords = "00000000-0000-4000-8000-000000000002";
    private static readonly HashSet<string> Types = ["text", "note", "email", "url", "date", "lookup", "person", "term", "keywords", "choice", "datetime", "integer", "decimal", "number", "currency", "boolean"];

    public async Task<IAsyncDisposable> LockAsync(CancellationToken ct)
    {
        var transaction = await lists.Database.BeginTransactionAsync(ct);
        try { await migrationLock.AcquireAsync(lists, tenant.TenantId!.Value, ct); return transaction; }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public async Task<(ContentMigrationPreview Preview, ContentImportBatch? Batch)> ReadAsync(Guid workspaceId, ContentSession session, CancellationToken ct)
    {
        var workspace = await workspaces.Workspaces.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking().Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == workspaceId, ct) ?? throw new KeyNotFoundException("Workspace not found.");
        var sourceLists = await lists.Lists.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking().Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.Id).ToListAsync(ct);
        var listIds = sourceLists.Select(x => x.Id).ToArray();
        var sourceItems = await lists.Items.IgnoreQueryFilters([QueryFilters.SoftDelete]).AsNoTracking().Where(x => listIds.Contains(x.ListId)).OrderBy(x => x.Id).ToListAsync(ct);
        var history = await lists.ItemVersions.AsNoTracking().Where(x => listIds.Contains(x.ListId)).OrderBy(x => x.Id).ToListAsync(ct);
        var typeIds = sourceLists.SelectMany(x => x.ContentTypeIds).Concat(history.Select(x => x.ContentTypeId)).Concat(sourceItems.Select(x => x.ContentTypeId)).Distinct().ToArray();
        var sourceTypes = await lists.ContentTypes.AsNoTracking().Where(x => typeIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        var acls = await lists.AclEntries.AsNoTracking().Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.ScopeId).ThenBy(x => x.PrincipalId).ToListAsync(ct);
        var files = await documents.FileVersions.AsNoTracking().Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.Id).ToListAsync(ct);
        var storedIds = files.Select(x => x.StoredFileId).Distinct().ToArray();
        var stored = await documents.StoredFiles.AsNoTracking().Where(x => storedIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        var sourceGroups = await taxonomy.Groups.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var sourceSets = await taxonomy.TermSets.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var terms = await taxonomy.Terms.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        // Deliberately project non-secret directory data: password hashes/tokens never enter a migration snapshot.
        var directoryUsers = await identity.Users.AsNoTracking().OrderBy(x => x.Id).Select(x => new
        {
            x.Id, x.CreatedAt, x.DeletedAt, Active = !x.IsDisabled && x.DeletedAt == null, Service = x.IsServiceAccount,
            DisplayName = x.DeletedAt != null ? "Deleted user" : x.DisplayName ?? x.UserName ?? "",
            Email = x.DeletedAt != null ? "" : x.Email ?? ""
        }).ToListAsync(ct);
        // Normalize timestamps after materialization: PostgreSQL's DateTimeOffset converter cannot coerce nullable UTC projections.
        var users = directoryUsers.Select(x => new IdentityUsersRow { Id = x.Id.ToString(), CreatedAt = At(x.CreatedAt),
            DeletedAt = x.DeletedAt is null ? null : At(x.DeletedAt.Value), Active = x.Active, Service = x.Service,
            DisplayName = x.DisplayName, Email = x.Email }).ToArray();
        var directoryGroups = await identity.Groups.AsNoTracking().OrderBy(x => x.Id).Select(x => new
        { x.Id, x.CreatedAt, x.Name, x.Description, x.Source, x.Version }).ToListAsync(ct);
        var groups = directoryGroups.Select(x => new IdentityGroupsRow { Id = x.Id.ToString(), CreatedAt = At(x.CreatedAt),
            Name = x.Name, Description = x.Description ?? "", Source = x.Source == GroupSource.Local ? "local" : "oidc", Version = x.Version }).ToArray();
        var memberships = await identity.GroupMembers.AsNoTracking().OrderBy(x => x.GroupId).ThenBy(x => x.UserId)
            .Select(x => new { x.GroupId, x.UserId }).ToListAsync(ct);
        var nestings = await identity.GroupNestings.AsNoTracking().OrderBy(x => x.GroupId).ThenBy(x => x.MemberGroupId).ToListAsync(ct);
        var blockers = new List<string>();
        foreach (var type in sourceTypes)
            foreach (var field in type.Fields.Where(x => x.Name != "title"))
                if (!Types.Contains(MapType(field.Type))) blockers.Add($"{type.Name}: field '{field.DisplayName}' uses unsupported extension type '{field.Type}'.");
        foreach (var list in sourceLists)
        {
            if (list.ContentTypeIds.Count == 0 || list.ContentTypeIds.Any(id => sourceTypes.All(x => x.Id != id))) blockers.Add($"{list.Name}: an attached content type is missing.");
            if (history.Any(x => x.ListId == list.Id && !list.ContentTypeIds.Contains(x.ContentTypeId))) blockers.Add($"{list.Name}: attach its historical content types before migration.");
            var fields = sourceTypes.Where(x => list.ContentTypeIds.Contains(x.Id)).SelectMany(x => x.Fields).Where(x => x.Name != "title").GroupBy(x => Key(x.Name));
            if (fields.Any(x => x.Select(y => JsonSerializer.Serialize(y, Json)).Distinct(StringComparer.Ordinal).Count() > 1)) blockers.Add($"{list.Name}: content types contain conflicting field definitions.");
        }
        foreach (var file in files)
            if (file.MediaType is not ("application/pdf" or "image/tiff" or "image/jpeg" or "image/png" or "image/webp")) blockers.Add($"File '{file.FileName}' has unsupported type '{file.MediaType}'.");
        if (files.Any(x => stored.All(s => s.Id != x.StoredFileId))) blockers.Add("A file version references missing stored-file metadata.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { workspace, sourceLists, sourceItems, history, sourceTypes, acls, files, stored, sourceGroups, sourceSets, terms, users, groups, memberships, nestings }, Json))));
        var already = await session.Db.Resources.AnyAsync(x => x.Id == workspace.Id.ToString(), ct);
        var preview = new ContentMigrationPreview(workspace.Id.ToString(), workspace.Name, hash, sourceLists.Count, sourceItems.Count, history.Count, files.Count, already, blockers);
        if (blockers.Count > 0) return (preview, null);
        var convertedGroups = sourceGroups.Where(x => !x.IsSystem).Select(x => new TermGroupsRow
        {
            Id = x.Id.ToString(),
            CreatedAt = At(x.CreatedAt),
            UpdatedAt = At(x.UpdatedAt),
            Name = x.Name,
            Description = x.Description ?? "",
            Version = Math.Max(1, x.Version)
        }).ToArray();
        var convertedSets = sourceSets.Where(x => !x.IsKeywords).Select(x => new TermSetsRow
        {
            Id = x.Id.ToString(),
            CreatedAt = At(x.CreatedAt),
            UpdatedAt = At(x.UpdatedAt),
            GroupId = sourceGroups.Single(g => g.Id == x.GroupId).IsSystem ? SystemGroup : x.GroupId.ToString(),
            Key = Key(x.Key ?? x.Name),
            Name = x.Name,
            Description = x.Description ?? "",
            IsOpen = x.IsOpen,
            Version = Math.Max(1, x.Version)
        }).ToArray();
        var keywordSets = sourceSets.Where(x => x.IsKeywords).Select(x => x.Id).ToHashSet();
        var convertedTerms = terms.Select(x => new TermsRow
        {
            Id = x.Id.ToString(),
            CreatedAt = At(x.CreatedAt),
            UpdatedAt = At(x.UpdatedAt),
            TermSetId = keywordSets.Contains(x.TermSetId) ? SystemKeywords : x.TermSetId.ToString(),
            ParentId = x.ParentId?.ToString(),
            Name = x.Name,
            NormalizedName = x.NormalizedName,
            Labels = JsonSerializer.Serialize(x.Labels.ToDictionary(l => l.Language, l => l.Name, StringComparer.Ordinal), Json),
            Synonyms = JsonSerializer.Serialize(x.Synonyms, Json),
            Description = x.Description ?? "",
            Color = x.Color,
            SortOrder = x.SortOrder,
            Path = x.Path,
            MergedIntoId = x.MergedIntoId?.ToString(),
            AvailableAsKeyword = x.AvailableAsKeyword || keywordSets.Contains(x.TermSetId),
            Deprecated = x.IsDeprecated,
            Version = Math.Max(1, x.Version)
        }).ToArray();
        var targetWorkspace = new ResourcesRow
        {
            Id = workspace.Id.ToString(),
            WorkspaceId = workspace.Id.ToString(),
            ScopeId = workspace.Id.ToString(),
            Kind = "workspace",
            Name = workspace.Name,
            Description = workspace.Description ?? "",
            InheritPermissions = false,
            PersonalOwnerId = workspace.PersonalOwnerId?.ToString(),
            CreatedAt = At(workspace.CreatedAt),
            UpdatedAt = At(workspace.UpdatedAt),
            CreatedBy = workspace.CreatedBy?.ToString() ?? "",
            UpdatedBy = workspace.UpdatedBy?.ToString() ?? "",
            Version = Math.Max(1, workspace.Version),
            DeletedAt = workspace.DeletedAt is null ? null : At(workspace.DeletedAt.Value),
            DeletedBy = workspace.DeletedBy?.ToString()
        };
        var converted = new List<ImportedCollectionSnapshot>();
        foreach (var list in sourceLists)
        {
            var attached = sourceTypes.Where(x => list.ContentTypeIds.Contains(x.Id)).ToArray();
            var schemaFields = attached.SelectMany(type => type.Fields.Where(x => x.Name != "title").Select(field =>
                new SchemaField(Key(field.Name), field.DisplayName, MapType(field.Type), field.Required, field.Choices, field.Indexed,
                    DefinitionId: Stable("field", Key(field.Name)), Options: new SchemaFieldOptions(Multiple: field.AllowMultiple || field.Type == "keywords",
                        MaxLength: field.MaxLength, Minimum: field.Minimum?.ToString(CultureInfo.InvariantCulture), Maximum: field.Maximum?.ToString(CultureInfo.InvariantCulture),
                        TermSetId: field.Type == "keywords" || field.TermSetId is { } termSet && keywordSets.Contains(termSet) ? SystemKeywords : field.TermSetId?.ToString(),
                        LookupContainerId: field.LookupListId?.ToString(), CurrencyCode: field.CurrencyCode, Description: field.Description,
                        DefaultValue: field.DefaultValue is null ? null : JsonSerializer.Deserialize<JsonElement>(field.DefaultValue)))))
                .DistinctBy(x => x.Id).ToArray();
            var schema = new EffectiveSchema(schemaFields, attached.Select(type => new SchemaContentType(type.Id.ToString(), "type_" + type.Id.ToString("N"), type.Name,
                type.Fields.Where(x => x.Name != "title").Select(x => Key(x.Name)).ToArray(), type.Id == list.ContentTypeIds[0])).ToArray());
            var container = new ResourcesRow
            {
                Id = list.Id.ToString(),
                WorkspaceId = targetWorkspace.Id,
                ParentId = targetWorkspace.Id,
                ScopeId = list.HasUniquePermissions ? list.Id.ToString() : targetWorkspace.Id,
                InheritPermissions = !list.HasUniquePermissions,
                Kind = list.Kind == ListKind.Library ? "library" : "list",
                Name = list.Name,
                Description = list.Description ?? "",
                AllowFolders = list.AllowFolders,
                Versioning = list.Versioning == ListVersioning.Major ? "major" : "off",
                MaxVersions = list.MaxVersions,
                SystemKey = list.SystemKey,
                CreatedAt = At(list.CreatedAt),
                UpdatedAt = At(list.UpdatedAt),
                CreatedBy = list.CreatedBy?.ToString() ?? "",
                UpdatedBy = list.UpdatedBy?.ToString() ?? "",
                Version = Math.Max(1, list.Version),
                DeletedAt = list.DeletedAt is null ? null : At(list.DeletedAt.Value),
                DeletedBy = list.DeletedBy?.ToString()
            };
            var imported = new List<ImportedContentItem>();
            foreach (var item in sourceItems.Where(x => x.ListId == list.Id))
            {
                var itemFiles = files.Where(x => x.ItemId == item.Id).ToArray();
                var currentFile = itemFiles.FirstOrDefault(x => x.IsCurrent);
                var payload = Payload(item.Fields);
                var keywords = Keywords(item.Fields, sourceTypes.Single(x => x.Id == item.ContentTypeId));
                var resource = new ResourcesRow
                {
                    Id = item.Id.ToString(),
                    WorkspaceId = targetWorkspace.Id,
                    ParentId = item.ParentId?.ToString() ?? container.Id,
                    ContainerId = container.Id,
                    ScopeId = item.HasUniquePermissions ? item.Id.ToString() : item.ScopeId == list.Id ? container.ScopeId : item.ScopeId.ToString(),
                    InheritPermissions = !item.HasUniquePermissions,
                    Kind = item.IsFolder ? "folder" : "item",
                    Name = item.Title,
                    Values = payload,
                    Keywords = keywords,
                    ContentTypeId = item.ContentTypeId.ToString(),
                    Version = Math.Max(1, item.Version),
                    CreatedAt = At(item.CreatedAt),
                    UpdatedAt = At(item.UpdatedAt),
                    CreatedBy = item.CreatedBy?.ToString() ?? "",
                    UpdatedBy = item.UpdatedBy?.ToString() ?? "",
                    DeletedAt = item.DeletedAt is null ? null : At(item.DeletedAt.Value),
                    DeletedBy = item.DeletedBy?.ToString(),
                    NameKey = container.Kind == "library" && item.DeletedAt is null ? item.Title.ToLowerInvariant() : null
                };
                var revisions = history.Where(x => x.ItemId == item.Id).OrderBy(x => x.Number).Select(x =>
                {
                    var file = itemFiles.Where(f => f.CreatedAt <= x.CreatedAt).OrderByDescending(f => f.CreatedAt).FirstOrDefault();
                    return new ImportedContentRevision(x.Id.ToString(), x.Title, x.ContentTypeId.ToString(), Payload(x.Fields), x.Number, At(x.CreatedAt), x.CreatedBy?.ToString() ?? "",
                        x.ChangedFields.Select(k => k == "title" ? "$name" : Key(k)).ToArray(), Keywords(x.Fields, sourceTypes.Single(t => t.Id == x.ContentTypeId)), file?.Id.ToString(), file?.FileName ?? "");
                }).ToArray();
                var head = new ImportedContentRevision(Guid.CreateVersion7().ToString(), item.Title, resource.ContentTypeId, payload,
                    revisions.Select(x => x.Number).DefaultIfEmpty(0).Max() + 1, resource.UpdatedAt, resource.UpdatedBy, [], keywords, currentFile?.Id.ToString(), currentFile?.FileName ?? "");
                var blobs = itemFiles.Select(file => new BlobsRow
                {
                    Id = file.Id.ToString(),
                    ItemId = resource.Id,
                    CreatedAt = At(file.CreatedAt),
                    Version = file.Number,
                    ObjectKey = stored.Single(x => x.Id == file.StoredFileId).BlobKey,
                    Filename = file.FileName,
                    ContentType = file.MediaType,
                    Languages = file.Languages ?? "",
                    Size = file.Size,
                    Sha256 = file.Sha256
                }).ToArray();
                imported.Add(new ImportedContentItem(resource, revisions, head, blobs));
            }
            converted.Add(new ImportedCollectionSnapshot(container, schema, imported));
        }
        var grants = new List<GrantsRow>();
        void Grant(string resourceId, string principalType, string principalId, WorkspaceAccessLevel level)
        {
            var actions = level switch { WorkspaceAccessLevel.Manage => new[] { "manage" }, WorkspaceAccessLevel.Contribute => ["read", "read_draft", "write"], WorkspaceAccessLevel.Read => ["read"], _ => [] };
            foreach (var action in actions) grants.Add(new GrantsRow { Id = Guid.CreateVersion7().ToString(), ResourceId = resourceId, PrincipalType = principalType, PrincipalId = principalId, Action = action });
        }
        foreach (var member in workspace.Members) Grant(targetWorkspace.Id, "user", member.UserId.ToString(), member.Role switch { WorkspaceRole.Owner => WorkspaceAccessLevel.Manage, WorkspaceRole.Member => WorkspaceAccessLevel.Contribute, _ => WorkspaceAccessLevel.Read });
        foreach (var acl in acls)
        {
            if (acl.PrincipalType is AclPrincipalType.User or AclPrincipalType.Group) Grant(acl.ScopeId.ToString(), acl.PrincipalType == AclPrincipalType.User ? "user" : "group", acl.PrincipalId.ToString(), acl.Level);
            else foreach (var member in workspace.Members.Where(x => acl.PrincipalType == AclPrincipalType.WorkspaceOwners && x.Role == WorkspaceRole.Owner
                || acl.PrincipalType == AclPrincipalType.WorkspaceMembers && x.Role == WorkspaceRole.Member || acl.PrincipalType == AclPrincipalType.WorkspaceVisitors && x.Role == WorkspaceRole.Visitor))
                Grant(acl.ScopeId.ToString(), "user", member.UserId.ToString(), acl.Level);
        }
        grants = grants.DistinctBy(x => (x.ResourceId, x.PrincipalType, x.PrincipalId, x.Action)).ToList();
        var targetMemberships = memberships.Select(x => new IdentityMembershipsRow { Id = Stable(x.GroupId.ToString(), x.UserId.ToString()), GroupId = x.GroupId.ToString(), PrincipalType = "user", PrincipalId = x.UserId.ToString() })
            .Concat(nestings.Select(x => new IdentityMembershipsRow { Id = Stable(x.GroupId.ToString(), x.MemberGroupId.ToString()), GroupId = x.GroupId.ToString(), PrincipalType = "group", PrincipalId = x.MemberGroupId.ToString() })).ToArray();
        return (preview,
            new ContentImportBatch(targetWorkspace, converted, users, groups, targetMemberships, convertedGroups, convertedSets, convertedTerms, grants, hash));
    }

    private static string Payload(string raw) => JsonSerializer.Serialize(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw)!
        .Where(x => x.Key != "title").ToDictionary(x => Key(x.Key), x => x.Value, StringComparer.Ordinal), Json);
    private static string Keywords(string raw, ContentType type)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw)!;
        return JsonSerializer.Serialize(type.Fields.Where(x => x.Type == "keywords" && values.ContainsKey(x.Name))
            .SelectMany(x => values[x.Name].ValueKind == JsonValueKind.Array ? values[x.Name].EnumerateArray().Select(v => v.GetString()!) : [])
            .Distinct(StringComparer.Ordinal).ToArray(), Json);
    }
    private static DateTime At(DateTimeOffset value) => value.UtcDateTime;
    private static string MapType(string value) => value == "managedMetadata" ? "term" : value;
    private static string Stable(string left, string right) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(left + ":" + right)).AsSpan(0, 16)).ToString();
    private static string Key(string value)
    {
        var key = NonKey().Replace(CamelCase().Replace(value, "$1_$2").ToLowerInvariant(), "_").Trim('_');
        if (key.Length == 0 || !char.IsAsciiLetterLower(key[0])) key = "field_" + key;
        return key.Length > 64 ? key[..48] + Stable("key", value).Replace("-", "", StringComparison.Ordinal)[..16] : key;
    }
    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelCase();
    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex NonKey();
}
