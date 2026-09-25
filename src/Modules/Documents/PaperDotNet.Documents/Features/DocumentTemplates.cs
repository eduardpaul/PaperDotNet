using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// Template section <c>LibrarySettings</c> in <c>urn:paperdotnet:documents:1</c> (PRV-05): a library's
/// duplicate policy and processing settings, and the group whose inbox it is (<c>GroupInbox</c>, by name). Contributed
/// through the SDK like an extension section.
/// </summary>
internal sealed class LibrarySettingsTemplateHandler(DocumentsDbContext db, IUserDirectory directory) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:documents:1";

    public XName Element => Ns + "LibrarySettings";

    public TemplateLevel Level => TemplateLevel.List;

    public int Order => 100;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var settings = await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(s => s.ListId == context.ListId, cancellationToken);
        var inbox = await db.GroupInboxes.AsNoTracking().FirstOrDefaultAsync(g => g.ListId == context.ListId, cancellationToken);
        var group = inbox is null ? null : (await directory.GetGroupNamesAsync([inbox.GroupId], cancellationToken)).GetValueOrDefault(inbox.GroupId);
        if (settings is null && group is null)
        {
            return null;
        }

        return new XElement(Element)
            .With("DuplicatePolicy", settings?.DuplicatePolicy)
            .With("AutoProcess", settings?.AutoProcess)
            .With("OcrMode", settings?.OcrMode)
            .With("OcrLanguages", settings?.OcrLanguages)
            .With("GroupInbox", group);
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var languages = section.Attr("OcrLanguages");
        if (languages is not null && !ProcessingScheduler.IsValidLanguageList(languages))
        {
            throw new TemplateException("OcrLanguages: Tesseract language codes joined with '+', e.g. 'deu+eng'.", section);
        }

        var settings = context.IsPlanned ? null : await db.LibrarySettings.FirstOrDefaultAsync(s => s.ListId == context.ListId, cancellationToken);
        var wanted = new LibrarySettings
        {
            Id = Ids.New(),
            ListId = context.ListId!.Value,
            DuplicatePolicy = section.EnumAttr("DuplicatePolicy", DuplicatePolicy.Warn),
            AutoProcess = section.BoolAttr("AutoProcess", true),
            OcrMode = section.EnumAttr("OcrMode", OcrMode.Auto),
            OcrLanguages = languages,
        };
        var name = $"{context.WorkspaceName}/{context.ListName}";
        if (settings is null)
        {
            context.Created(TemplateKinds.Settings, $"{name}: library settings");
            if (!context.DryRun)
            {
                db.LibrarySettings.Add(wanted);
            }
        }
        else if (settings.DuplicatePolicy != wanted.DuplicatePolicy || settings.AutoProcess != wanted.AutoProcess
            || settings.OcrMode != wanted.OcrMode || settings.OcrLanguages != wanted.OcrLanguages)
        {
            context.Updated(TemplateKinds.Settings, $"{name}: library settings");
            settings.DuplicatePolicy = wanted.DuplicatePolicy;
            settings.AutoProcess = wanted.AutoProcess;
            settings.OcrMode = wanted.OcrMode;
            settings.OcrLanguages = wanted.OcrLanguages;
        }

        await ApplyGroupInboxAsync(section, context, name, cancellationToken);
        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Makes the library the inbox of the named group (DOC-16); a group's inbox moves here if it was elsewhere.</summary>
    private async Task ApplyGroupInboxAsync(XElement section, TemplateContext context, string name, CancellationToken ct)
    {
        if (section.Attr("GroupInbox") is not { } groupName)
        {
            return;
        }

        if (await directory.FindGroupAsync(groupName, ct) is not { } groupId)
        {
            context.Warn($"{name}: the group '{groupName}' does not exist, so the library is not its inbox.", section);
            return;
        }

        var inbox = await db.GroupInboxes.FirstOrDefaultAsync(g => g.GroupId == groupId, ct);
        if (inbox is not null && inbox.ListId == context.ListId)
        {
            return;
        }

        context.Updated(TemplateKinds.Settings, $"{name}: inbox of group {groupName}");
        if (context.DryRun)
        {
            return;
        }

        if (inbox is null)
        {
            inbox = new GroupInbox { Id = Ids.New(), GroupId = groupId };
            db.GroupInboxes.Add(inbox);
        }

        inbox.WorkspaceId = context.WorkspaceId!.Value;
        inbox.ListId = context.ListId!.Value;
    }
}

/// <summary>What a package says about one file version (PLT-13/15).</summary>
internal sealed record ImportedVersion(
    string? Source, string? Languages, string? TextLanguage, IReadOnlyList<string>? Pages, AuditStamp? Stamp, bool Processed = false);

/// <summary>
/// List section <c>Files</c> in <c>urn:paperdotnet:documents:1</c> (PRV-04): the current file of each document, in the
/// template package (content stored once) with a JSON document mapping item keys to files. Apply gives the items created
/// from the package's <c>Items</c> section their file, unless they have one; files are checked and processed like uploads.
/// Each entry also lists all <c>versions</c>, oldest first, with source, languages, stamps and page texts (a JSON array of
/// strings stored as a package file): versions with texts are not processed again.
/// </summary>
internal sealed class DocumentFilesTemplateHandler(
    DocumentsDbContext db, IBlobStore blobs, IListItemStore items, DocumentService documents, IUserDirectory directory) : ITemplateHandler
{
    public XName Element => LibrarySettingsTemplateHandler.Ns + "Files";

    public TemplateLevel Level => TemplateLevel.List;

    /// <summary>After the items (800).</summary>
    public int Order => 850;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        if (context.Package is not { } package)
        {
            return null;
        }

        var listId = context.ListId!.Value;
        var versions = await (from v in db.FileVersions.AsNoTracking()
                              where v.ListId == listId
                              join f in db.StoredFiles.AsNoTracking() on v.StoredFileId equals f.Id
                              orderby v.ItemId, v.Number
                              select new { Version = v, File = f })
            .ToListAsync(cancellationToken);
        var people = await directory.GetUserNamesAsync([.. versions.Select(v => v.Version.CreatedBy).OfType<Guid>().Distinct()], cancellationToken);
        var entries = new JsonArray();
        foreach (var item in versions.GroupBy(v => v.Version.ItemId))
        {
            var exported = new JsonArray();
            foreach (var (version, stored) in item.Select(v => (v.Version, v.File)))
            {
                await using var content = await blobs.OpenReadAsync(stored.BlobKey, cancellationToken);
                if (content is null)
                {
                    context.Warn($"{context.WorkspaceName}/{context.ListName}: the file '{version.FileName}' (version {version.Number}) is missing in storage and was left out.");
                    continue;
                }

                var texts = await db.Pages.AsNoTracking().Where(p => p.StoredFileId == stored.Id).OrderBy(p => p.PageNumber).ToListAsync(cancellationToken);
                string? pages = null;
                if (texts.Count > 0)
                {
                    var array = new JsonArray();
                    var number = 1;
                    foreach (var page in texts)
                    {
                        while (number++ < page.PageNumber)
                        {
                            array.Add(string.Empty);
                        }

                        array.Add(page.Text);
                    }

                    using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(array.ToJsonString()));
                    pages = await package.AddFileAsync(json, cancellationToken);
                }

                exported.Add(new JsonObject
                {
                    ["file"] = await package.AddFileAsync(content, cancellationToken),
                    ["name"] = version.FileName,
                    ["source"] = version.Source,
                    ["created"] = version.CreatedAt,
                    ["createdBy"] = version.CreatedBy is { } by ? people.GetValueOrDefault(by) : null,
                    ["languages"] = version.Languages,
                    ["textLanguage"] = version.TextLanguage,
                    ["pages"] = pages,
                    ["processed"] = version.ProcessingStatus == ProcessingStatus.Succeeded ? true : null,
                    ["current"] = version.IsCurrent ? true : null,
                });
            }

            if (exported.Count == 0)
            {
                continue;
            }

            // The current file stays at the top level, so older readers still find it.
            var current = exported.OfType<JsonObject>().LastOrDefault(v => v["current"] is not null) ?? (JsonObject)exported[^1]!;
            entries.Add(new JsonObject
            {
                ["key"] = item.Key.ToString("N"),
                ["file"] = current["file"]!.DeepClone(),
                ["name"] = current["name"]!.DeepClone(),
                ["versions"] = exported,
            });
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var path = $"content/files-{listId:N}.json";
        await package.WriteJsonAsync(path, new JsonObject { ["files"] = entries }, cancellationToken);
        return new XElement(Element).With("File", path).With("Count", entries.Count);
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var package = context.RequirePackage(section);
        var file = section.RequiredAttr("File");
        var document = await package.ReadJsonAsync(file, cancellationToken) ?? throw new TemplateException($"The package has no {file}.", section);
        var entries = (document["files"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var name = $"{context.WorkspaceName}/{context.ListName}";
        if (context.IsPlanned)
        {
            context.Created("files", name, $"{entries.Count} files");
            return;
        }

        var listId = context.ListId!.Value;
        var added = 0;
        foreach (var entry in entries)
        {
            if (entry["key"]?.ToString() is not { Length: > 0 } key || entry["file"]?.ToString() is not { Length: > 0 } path)
            {
                throw new TemplateException($"{file}: every file needs a key and a file.", section);
            }

            var itemId = TemplateContent.ItemId(listId, key);
            if (await db.FileVersions.AnyAsync(v => v.ItemId == itemId, cancellationToken))
            {
                continue;
            }

            if (context.DryRun)
            {
                added++;
                continue;
            }

            var item = await items.AsSystem().GetAsync(context.WorkspaceId!.Value, listId, itemId, cancellationToken);
            if (item is null)
            {
                context.Warn($"{name}: the file of item {key} was skipped: the item was not created.");
                continue;
            }

            if (entry["versions"] is JsonArray versions && versions.Count > 0)
            {
                if (await ImportVersionsAsync(item, versions.OfType<JsonObject>().ToList(), package, cancellationToken) is { } versionError)
                {
                    context.Warn($"{name}: the file of item {key} was not completely imported: {versionError}.");
                }

                added++;
                continue;
            }

            await using var content = package.OpenFile(path);
            if (content is null)
            {
                context.Warn($"{name}: the file of item {key} was skipped: the package has no {path}.");
                continue;
            }

            if (await documents.AttachAsync(item, content, entry["name"]?.ToString() ?? "document", cancellationToken) is { } error)
            {
                context.Warn($"{name}: the file of item {key} was skipped: {error}.");
                continue;
            }

            added++;
        }

        if (added > 0)
        {
            context.Created("files", name, $"{added} files");
        }
    }

    /// <summary>Imports every version in order (the last one becomes current); returns the first error.</summary>
    private async Task<string?> ImportVersionsAsync(ListItemData item, List<JsonObject> versions, ITemplatePackage package, CancellationToken ct)
    {
        for (var index = 0; index < versions.Count; index++)
        {
            var version = versions[index];
            if (version["file"]?.ToString() is not { Length: > 0 } path || package.OpenFile(path) is not { } content)
            {
                return $"version {index + 1} has no file in the package";
            }

            await using (content)
            {
                IReadOnlyList<string>? pages = null;
                if (version["pages"]?.ToString() is { Length: > 0 } pagesPath && package.OpenFile(pagesPath) is { } pagesStream)
                {
                    await using (pagesStream)
                    {
                        pages = (await JsonNode.ParseAsync(pagesStream, cancellationToken: ct) as JsonArray)?.Select(p => p?.ToString() ?? string.Empty).ToList();
                    }
                }

                var languages = version["languages"]?.ToString();
                var textLanguage = version["textLanguage"]?.ToString();
                var imported = new ImportedVersion(
                    version["source"]?.ToString(),
                    languages is not null && ProcessingScheduler.IsValidLanguageList(languages) ? languages : null,
                    textLanguage is { Length: > 0 and <= 20 } ? textLanguage : null,
                    pages,
                    await StampAsync(version, ct),
                    version["processed"] is JsonValue processed && processed.TryGetValue<bool>(out var done) && done);
                if (await documents.ImportVersionAsync(item, content, version["name"]?.ToString() ?? "document", imported, index == versions.Count - 1, ct) is { } error)
                {
                    return $"version {index + 1}: {error}";
                }
            }
        }

        return null;
    }

    private async Task<AuditStamp?> StampAsync(JsonObject version, CancellationToken ct) =>
        version["created"] is JsonValue created && created.TryGetValue<DateTimeOffset>(out var at)
            ? new AuditStamp(at, version["createdBy"]?.ToString() is { Length: > 0 } userName ? await directory.FindUserAsync(userName, ct) : null)
            : null;
}
