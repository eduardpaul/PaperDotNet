using PaperDotNet.Notes.Features;

namespace PaperDotNet.UnitTests;

public sealed class NoteMarkdownTests
{
    [Fact]
    public void Tags_follow_obsidian_rules()
    {
        const string Text = "# Heading\n#one and #Two/nested, not#this, #123 or `#code`\n```\n#fenced\n```\nsee http://x.org/#anchor and #one again and [[Note#Heading]] #end-";
        Assert.Equal(["one", "Two/nested", "end"], NoteMarkdown.Tags(Text));
    }

    [Fact]
    public void Wiki_links_have_targets_headings_aliases_and_embeds()
    {
        var links = NoteMarkdown.Links("[[A]] [[Folder/B.md#Part|shown]] ![[image.png]] `[[code]]` [[ ]]");

        Assert.Equal(
            [new WikiLink("A", null, null, false), new WikiLink("Folder/B.md", "Part", "shown", false), new WikiLink("image.png", null, null, true)],
            links);
        Assert.Equal("b", NoteMarkdown.Normalize(" Folder/B.md "));
    }

    [Fact]
    public void Renames_rewrite_matching_links_outside_code()
    {
        const string Text = "[[Old]] [[old#H|a]] ![[Folder/Old]] [[Other]] `[[Old]]`";
        Assert.Equal("[[New]] [[New#H|a]] ![[New]] [[Other]] `[[Old]]`", NoteMarkdown.RenameLinks(Text, "Old", "New"));
    }
}
