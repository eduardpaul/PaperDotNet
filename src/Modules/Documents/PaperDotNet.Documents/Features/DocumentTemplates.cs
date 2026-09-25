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

/// <summary>
/// List section <c>Files</c> in <c>urn:paperdotnet:documents:1</c> (PRV-04): the current file of each document, in the
/// template package (content stored once) with a JSON document mapping item keys to files. Apply gives the items created
/// from the package's <c>Items</c> section their file, unless they have one; files are checked and processed like uploads.
/// Earlier versions are not included.
/// </summary>
internal sealed class DocumentFilesTemplateHandler(DocumentsDbContext db, IBlobStore blobs, IListItemStore items, DocumentService documents)
    : ITemplateHandler
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
                              where v.ListId == listId && v.IsCurrent
                              join f in db.StoredFiles.AsNoTracking() on v.StoredFileId equals f.Id
                              orderby v.ItemId
                              select new { v.ItemId, v.FileName, File = f })
            .ToListAsync(cancellationToken);
        var entries = new JsonArray();
        foreach (var version in versions)
        {
            await using var content = await blobs.OpenReadAsync(version.File.BlobKey, cancellationToken);
            if (content is null)
            {
                context.Warn($"{context.WorkspaceName}/{context.ListName}: the file '{version.FileName}' is missing in storage and was left out.");
                continue;
            }

            entries.Add(new JsonObject
            {
                ["key"] = version.ItemId.ToString("N"),
                ["file"] = await package.AddFileAsync(content, cancellationToken),
                ["name"] = version.FileName,
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
            await using var content = package.OpenFile(path);
            if (item is null || content is null)
            {
                context.Warn($"{name}: the file of item {key} was skipped: {(item is null ? "the item was not created" : $"the package has no {path}")}.");
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
}
