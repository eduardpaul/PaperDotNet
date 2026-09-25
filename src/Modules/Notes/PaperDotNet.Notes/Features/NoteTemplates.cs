using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.Notes.Features;

/// <summary>The note content type and the Notes list template (LST-18), registered like built-in ones.</summary>
public static class NoteTemplates
{
    public const string ContentTypeKey = "note";
    public const string ListTemplateKey = "notes";
    public const string BodyField = "body";
    public const string TagsField = "tags";

    public static readonly ContentTypeTemplate ContentType = new(ContentTypeKey, "Note", "A Markdown note with #tags and [[wiki links]].",
    [
        new FieldDefinition { Name = BodyField, DisplayName = "Body", Type = "note", MaxLength = 100_000 },
        new FieldDefinition { Name = TagsField, DisplayName = "Tags", Type = "keywords", AllowMultiple = true, Search = FieldSearchWeight.High },
    ]);

    public static readonly ListTemplateDefinition List = new(ListTemplateKey, "Notes", "Markdown notes with tags, wiki links and backlinks.", [ContentTypeKey],
    [
        new ViewTemplate("All notes", ["title", "tags"], OrderBy: "updatedAt desc", IsDefault: true),
    ]);

    internal static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>
/// Adds the <c>#tags</c> of a note's body to its <c>tags</c> (keywords) when the body changes (LST-18). Tags removed
/// from <c>tags</c> by hand stay removed until the body changes again; removing a <c>#tag</c> from the body keeps the tag.
/// </summary>
internal sealed class NoteTagsMutator : IItemMutator
{
    public int Sequence => 60;

    public bool AppliesTo(ItemEventScope scope) => !scope.IsFolder && scope.ContentTypeKey == NoteTemplates.ContentTypeKey;

    public ValueTask ItemAddingAsync(ItemMutationContext context, CancellationToken cancellationToken) => ApplyAsync(context);

    public ValueTask ItemUpdatingAsync(ItemMutationContext context, CancellationToken cancellationToken) => ApplyAsync(context);

    private static ValueTask ApplyAsync(ItemMutationContext context)
    {
        var body = NoteTemplates.Text(context.After?[NoteTemplates.BodyField]);
        if (body is null || body == NoteTemplates.Text(context.Before?[NoteTemplates.BodyField]))
        {
            return ValueTask.CompletedTask;
        }

        var tags = NoteMarkdown.Tags(body);
        if (tags.Count > 0)
        {
            var values = context.After![NoteTemplates.TagsField] is JsonArray current ? new JsonArray([.. current.Select(v => v?.DeepClone())]) : [];
            foreach (var tag in tags)
            {
                values.Add(JsonValue.Create(tag));
            }

            context.After[NoteTemplates.TagsField] = values;
        }

        return ValueTask.CompletedTask;
    }
}
