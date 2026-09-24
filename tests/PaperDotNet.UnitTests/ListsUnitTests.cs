using System.Text.Json;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Lists.Querying;

namespace PaperDotNet.UnitTests;

public sealed class ListsUnitTests
{
    private static readonly FieldTypeRegistry Registry = new(
    [
        new TextFieldType(), new NumberFieldType(), new DateFieldType(), new DateTimeFieldType(), new ChoiceFieldType(),
    ]);

    [Fact]
    public void Item_cursors_round_trip()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(id, ItemCursor.Decode(ItemCursor.Keyset(id)).After);
        Assert.Equal(40, ItemCursor.Decode(ItemCursor.ForOffset(40)).Offset);
        Assert.Equal(default, ItemCursor.Decode("garbage"));
    }

    [Theory]
    [InlineData("dateTime", "\"2026-09-24T10:00:00+02:00\"", "\"2026-09-24T08:00:00.0000000Z\"")]
    [InlineData("date", "\"2026-02-03\"", "\"2026-02-03\"")]
    [InlineData("number", "12.50", "12.50")]
    public async Task Values_are_normalized_to_canonical_form(string type, string input, string expected)
    {
        var field = new FieldDefinition { Name = "x", DisplayName = "X", Type = type };
        using var json = JsonDocument.Parse(input);

        var result = await Registry.Find(type)!.NormalizeAsync(json.RootElement, field, NoLookups.Instance, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Value!.ToJsonString());
    }

    [Theory]
    [InlineData("dateTime", "\"2026-09-24\"")]
    [InlineData("date", "\"24.09.2026\"")]
    [InlineData("number", "\"12\"")]
    [InlineData("choice", "\"maybe\"")]
    public async Task Invalid_values_are_rejected(string type, string input)
    {
        var field = new FieldDefinition { Name = "x", DisplayName = "X", Type = type, Choices = ["yes", "no"] };
        using var json = JsonDocument.Parse(input);

        var result = await Registry.Find(type)!.NormalizeAsync(json.RootElement, field, NoLookups.Instance, CancellationToken.None);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Reserved_and_malformed_field_names_are_rejected()
    {
        Assert.NotEmpty(Registry.Validate(new FieldDefinition { Name = "createdAt", DisplayName = "x", Type = "text" }));
        Assert.NotEmpty(Registry.Validate(new FieldDefinition { Name = "Amount", DisplayName = "x", Type = "text" }));
        Assert.Empty(Registry.Validate(new FieldDefinition { Name = "amount", DisplayName = "Amount", Type = "number" }));
    }

    private sealed class NoLookups : IFieldValidationContext
    {
        public static readonly NoLookups Instance = new();

        public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ItemExistsAsync(Guid listId, Guid itemId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<Guid?> ResolveTermAsync(Guid? termSetId, string value, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
    }
}
