using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Data;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// Template section <c>LibrarySettings</c> in <c>urn:paperdotnet:documents:1</c> (PRV-05): a library's
/// duplicate policy and processing settings. Contributed through the SDK like an extension section.
/// </summary>
internal sealed class LibrarySettingsTemplateHandler(DocumentsDbContext db) : ITemplateHandler
{
    public static readonly XNamespace Ns = "urn:paperdotnet:documents:1";

    public XName Element => Ns + "LibrarySettings";

    public TemplateLevel Level => TemplateLevel.List;

    public int Order => 100;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var settings = await db.LibrarySettings.AsNoTracking().FirstOrDefaultAsync(s => s.ListId == context.ListId, cancellationToken);
        return settings is null ? null : new XElement(Element)
            .With("DuplicatePolicy", settings.DuplicatePolicy)
            .With("AutoProcess", settings.AutoProcess)
            .With("OcrMode", settings.OcrMode)
            .With("OcrLanguages", settings.OcrLanguages);
    }

    public async Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        var languages = section.Attr("OcrLanguages") ?? LibrarySettings.DefaultOcrLanguages;
        if (!ProcessingScheduler.IsValidLanguageList(languages))
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

        if (!context.DryRun)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
