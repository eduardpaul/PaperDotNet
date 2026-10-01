using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Notes.Features;

/// <summary>The note content type and the Notes list template (LST-18), registered like built-in ones.</summary>
public static class NoteTemplates
{
    public const string ContentTypeKey = "note";
    public const string ListTemplateKey = "notes";
    public const string BodyField = "body";

    public static readonly ContentTypeTemplate ContentType = new(ContentTypeKey, "Note", "A Markdown note with #tags and [[wiki links]].",
    [
        // #tags as keywords (a "tags" field and the mutator that fills it) come with Taxonomy (T12).
        new FieldDefinition { Name = BodyField, DisplayName = "Body", Type = "note", MaxLength = 100_000 },
    ]);

    public static readonly ListTemplateDefinition List = new(ListTemplateKey, "Notes", "Markdown notes with wiki links and backlinks.", [ContentTypeKey],
    [
        new ViewTemplate("All notes", ["title"], OrderBy: "updatedAt desc", IsDefault: true),
    ]);

    internal static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
}
