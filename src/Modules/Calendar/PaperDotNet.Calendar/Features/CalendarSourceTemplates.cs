using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Calendar.Data;
using PaperDotNet.Provisioning.Contracts;

namespace PaperDotNet.Calendar.Features;

/// <summary>Portable source labels; the credential URLs must always be supplied at the destination.</summary>
internal sealed class CalendarSourceTemplateHandler(CalendarDbContext db) : ITemplateHandler
{
    private static readonly XNamespace Ns = "urn:paperdotnet:calendar:1";
    public XName Element => Ns + "CalendarSources";
    public TemplateLevel Level => TemplateLevel.List;
    public int Order => 100;

    public async Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken)
    {
        var names = await db.Subscriptions.AsNoTracking().Where(s => s.ListId == context.ListId)
            .OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(cancellationToken);
        return names.Count == 0 ? null : new XElement(Element,
            names.Select(name => new XElement(Ns + "Source", new XAttribute("Name", name))));
    }

    public Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken)
    {
        foreach (var source in section.Elements(Ns + "Source"))
        {
            var name = (string?)source.Attribute("Name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            {
                throw new TemplateException("Calendar source names must have 1 to 200 characters.", source);
            }

            context.Warn($"{context.WorkspaceName}/{context.ListName}: reconnect calendar source '{name}' by adding its URL in Calendar sources. Secret feed URLs are not included in templates.", source);
        }

        return Task.CompletedTask;
    }
}
