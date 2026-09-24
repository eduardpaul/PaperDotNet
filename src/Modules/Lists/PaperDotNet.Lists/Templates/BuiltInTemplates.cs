using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Lists.Templates;

/// <summary>
/// Built-in content types and list templates (LST-16). Apps built on the SDK (Tasks) register theirs
/// the same way; extensions add more through the SDK.
/// </summary>
internal static class BuiltInTemplates
{
    private static FieldDefinition Field(string name, string displayName, string type, Action<FieldDefinition>? configure = null)
    {
        var field = new FieldDefinition { Name = name, DisplayName = displayName, Type = type };
        configure?.Invoke(field);
        return field;
    }

    public static readonly ContentTypeTemplate[] ContentTypes =
    [
        new("document", "Document", "A document with a description and keywords.",
        [
            Field("description", "Description", "note"),
            Field("keywords", "Keywords", "keywords", f => { f.AllowMultiple = true; f.Search = FieldSearchWeight.High; }),
        ]),
        new("event", "Event", "A calendar event.",
        [
            Field("start", "Start", "dateTime", f => f.Required = true),
            Field("end", "End", "dateTime"),
            Field("allDay", "All day", "boolean"),
            Field("location", "Location", "text"),
            Field("description", "Description", "note"),
        ]),
        new("contact", "Contact", "A person or organization to contact.",
        [
            Field("email", "E-mail", "email", f => f.Search = FieldSearchWeight.High),
            Field("phone", "Phone", "text"),
            Field("company", "Company", "text", f => f.Search = FieldSearchWeight.High),
            Field("jobTitle", "Job title", "text"),
            Field("notes", "Notes", "note"),
        ]),
        new("note", "Note", "A note with free tags.",
        [
            Field("body", "Body", "note", f => f.MaxLength = 100_000),
            Field("tags", "Tags", "keywords", f => { f.AllowMultiple = true; f.Search = FieldSearchWeight.High; }),
        ]),
    ];

    public static readonly ListTemplateDefinition[] Lists =
    [
        new("documents", "Documents", "A document library with keywords and version history.", ["document"],
        [
            new ViewTemplate("All documents", ["title", "description", "keywords"], IsDefault: true),
        ])
        {
            IsLibrary = true,
            Versioning = true,
        },
        new("calendar", "Calendar", "Events on a calendar.", ["event"],
        [
            new ViewTemplate("Calendar", ["title", "start", "end", "allDay", "location"], OrderBy: "fields/start", GroupBy: "start", Layout: "calendar", IsDefault: true),
            new ViewTemplate("All events", ["title", "start", "end", "location"], OrderBy: "fields/start desc"),
        ]),
        new("contacts", "Contacts", "People and organizations.", ["contact"],
        [
            new ViewTemplate("All contacts", ["title", "company", "email", "phone"], OrderBy: "fields/title", IsDefault: true),
        ]),
        new("notes", "Notes", "Notes with free tags.", ["note"],
        [
            new ViewTemplate("All notes", ["title", "tags"], OrderBy: "updatedAt desc", IsDefault: true),
        ]),
    ];
}
