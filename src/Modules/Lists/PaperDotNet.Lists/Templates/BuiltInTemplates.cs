using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Lists.Templates;

/// <summary>
/// Built-in content types and list templates (LST-16). Apps built on the SDK (Tasks, Calendar, Notes) register theirs
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
        new("contact", "Contact", "A person or organization to contact.",
        [
            Field("email", "E-mail", "email", f => f.Search = FieldSearchWeight.High),
            Field("phone", "Phone", "text"),
            Field("company", "Company", "text", f => f.Search = FieldSearchWeight.High),
            Field("jobTitle", "Job title", "text"),
            Field("notes", "Notes", "note"),
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
        new("contacts", "Contacts", "People and organizations.", ["contact"],
        [
            new ViewTemplate("All contacts", ["title", "company", "email", "phone"], OrderBy: "fields/title", IsDefault: true),
        ]),
    ];
}
