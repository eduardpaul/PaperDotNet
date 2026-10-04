using System.Text.Json.Nodes;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Taxonomy.Contracts;
using PaperDotNet.Workflows.Features;

namespace PaperDotNet.UnitTests;

public sealed class TriggerConditionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonObject Node(string json) => JsonNode.Parse(json)!.AsObject();
    private static readonly Guid Tag = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Child = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ItemSnapshot Snapshot(string fields, string type = "text", bool multiple = false) => new(
        Guid.Empty, Guid.Empty, Guid.Empty, "List", Guid.Empty, "Type", "type", null, false, Node(fields),
        new Dictionary<string, ItemSnapshotField>
        {
            ["value"] = new(type, multiple),
            ["tags"] = new("managedMetadata", true),
            ["keyword"] = new("keywords", true),
        });

    private static ItemUpdated Update(ItemSnapshot before, ItemSnapshot after) => new()
    {
        TenantId = Guid.Empty,
        TenantIdentifier = "test",
        WorkspaceId = Guid.Empty,
        ListId = Guid.Empty,
        ItemId = Guid.Empty,
        ContentTypeId = Guid.Empty,
        Before = before,
        After = after,
    };

    [Theory]
    [InlineData("eq", "same", "same", true)]
    [InlineData("neq", "same", "other", true)]
    [InlineData("contains", "abc", "b", true)]
    [InlineData("startsWith", "abc", "A", false)]
    [InlineData("endsWith", "abc", "c", true)]
    [InlineData("gt", "a", "Z", true)]
    [InlineData("gte", "same", "same", true)]
    [InlineData("lt", "Z", "a", true)]
    [InlineData("lte", "z", "A", false)]
    public async Task Text_comparisons_use_ordinal_semantics(string op, string value, string operand, bool expected)
    {
        var condition = new JsonObject { ["target"] = "field", ["field"] = "value", ["operator"] = op, ["value"] = operand };
        var snapshot = Snapshot(new JsonObject { ["value"] = value }.ToJsonString());
        Assert.Equal(expected, await TriggerConditions.MatchesAsync(condition, Update(snapshot, snapshot), new Terms(), [], Ct));
    }

    [Theory]
    [InlineData("number", "2", "10", "lt", true)]
    [InlineData("number", "2.0", "2", "eq", true)]
    [InlineData("currency", "10.01", "10.00", "gt", true)]
    [InlineData("date", "\"2026-10-04\"", "\"2026-10-05\"", "lte", true)]
    [InlineData("dateTime", "\"2026-10-04T12:00:00+02:00\"", "\"2026-10-04T10:00:00Z\"", "eq", true)]
    [InlineData("number", "2", "\"2\"", "neq", false)]
    public async Task Typed_values_compare_without_string_coercion(string type, string value, string operand, string op, bool expected)
    {
        var condition = Node($$"""{"target":"field","field":"value","operator":"{{op}}","value":{{operand}}} """);
        var snapshot = Snapshot($$"""{"value":{{value}}} """, type);
        Assert.Equal(expected, await TriggerConditions.MatchesAsync(condition, Update(snapshot, snapshot), new Terms(), [], Ct));
    }

    [Fact]
    public async Task Collections_use_sets_and_transitions_use_both_snapshots()
    {
        var before = Snapshot("""{"value":["a","b"]}""", "choice", true);
        var after = Snapshot("""{"value":["b","a"]}""", "choice", true);
        var source = Update(before, after);
        Assert.False(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"changed"}"""), source, new Terms(), [], Ct));
        Assert.False(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"added"}"""), source, new Terms(), [], Ct));
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"containsAll","value":["a","b"]}"""), source, new Terms(), [], Ct));
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"containsAny","value":["missing","b"]}"""), source, new Terms(), [], Ct));
        source = Update(before, Snapshot("""{"value":["b","c"]}""", "choice", true));
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"added","value":"c"}"""), source, new Terms(), [], Ct));
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"removed","value":"a"}"""), source, new Terms(), [], Ct));
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"transition","from":["b","a"],"to":["c","b"]}"""), source, new Terms(), [], Ct));
    }

    [Fact]
    public async Task Collection_sets_normalize_numeric_scales_and_reference_formats()
    {
        var condition = Node("""{"target":"field","field":"value","operator":"changed"}""");
        var source = Update(Snapshot("""{"value":[1.0,2.00]}""", "number", true), Snapshot("""{"value":[2,1]}""", "number", true));
        Assert.False(await TriggerConditions.MatchesAsync(condition, source, new Terms(), [], Ct));
        source = Update(Snapshot(new JsonObject { ["value"] = new JsonArray(Tag.ToString("D")) }.ToJsonString(), "lookup", true),
            Snapshot(new JsonObject { ["value"] = new JsonArray(Tag.ToString("N")) }.ToJsonString(), "lookup", true));
        Assert.False(await TriggerConditions.MatchesAsync(condition, source, new Terms(), [], Ct));
    }

    [Fact]
    public async Task Missing_fields_do_not_match_negative_or_empty_conditions_and_groups_short_circuit()
    {
        var snapshot = Snapshot("{}");
        var source = Update(snapshot, snapshot);
        Assert.False(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"missing","operator":"neq","value":"x"}"""), source, new Terms(), [], Ct));
        Assert.False(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"missing","operator":"isEmpty"}"""), source, new Terms(), [], Ct));
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"isEmpty"}"""), source, new Terms(), [], Ct));
        var terms = new Terms();
        Assert.False(await TriggerConditions.MatchesAsync(Node("""{"all":[{"target":"field","field":"missing","operator":"eq","value":"x"},{"target":"tags","operator":"contains","term":"Group/Set/Parent"}]}"""), source, terms, [], Ct));
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"any":[{"target":"field","field":"value","operator":"isEmpty"},{"target":"tags","operator":"contains","term":"Group/Set/Parent"}]}"""), source, terms, [], Ct));
        Assert.Equal(0, terms.Lookups);
        source = source with { After = null };
        Assert.False(await TriggerConditions.MatchesAsync(Node("""{"target":"field","field":"value","operator":"isEmpty"}"""), source, terms, [], Ct));
    }

    [Fact]
    public async Task Tags_include_keywords_and_cache_term_resolution_with_optional_descendants()
    {
        var empty = Snapshot("{}");
        var assigned = Snapshot(new JsonObject { ["keyword"] = new JsonArray(Child.ToString()) }.ToJsonString());
        var source = Update(empty, assigned);
        var condition = Node("""{"target":"tags","operator":"added","term":"Group/Set/Parent"}""");
        var terms = new Terms();
        var cache = new Dictionary<string, HashSet<Guid>>();
        Assert.True(await TriggerConditions.MatchesAsync(condition, source, terms, cache, Ct));
        Assert.True(await TriggerConditions.MatchesAsync(condition, source, terms, cache, Ct));
        Assert.Equal(1, terms.Lookups);
        condition["includeDescendants"] = false;
        Assert.False(await TriggerConditions.MatchesAsync(condition, source, terms, cache, Ct));
        Assert.False(await TriggerConditions.MatchesAsync(Node("""{"target":"tags","operator":"added"}"""), Update(assigned, assigned), terms, cache, Ct));
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"target":"tags","operator":"removed"}"""), Update(assigned, empty), terms, cache, Ct));
        var created = new ItemAdded { TenantId = Guid.Empty, TenantIdentifier = "test", WorkspaceId = Guid.Empty, ListId = Guid.Empty, ItemId = Guid.Empty, ContentTypeId = Guid.Empty, After = assigned };
        Assert.True(await TriggerConditions.MatchesAsync(Node("""{"target":"tags","operator":"added"}"""), created, terms, cache, Ct));
    }

    [Theory]
    [InlineData("itemAdded", "{\"target\":\"field\",\"field\":\"value\",\"operator\":\"changed\"}")]
    [InlineData("itemAdded", "{\"target\":\"tags\",\"operator\":\"removed\"}")]
    [InlineData("manual", "{\"target\":\"tags\",\"operator\":\"added\"}")]
    [InlineData("itemUpdated", "{\"all\":[]}")]
    [InlineData("itemUpdated", "{\"all\":[{}],\"any\":[{}]}")]
    [InlineData("itemUpdated", "{\"target\":\"field\",\"field\":\"value\",\"operator\":\"containsAny\",\"value\":[]}")]
    [InlineData("itemUpdated", "{\"target\":\"field\",\"field\":\"value\",\"operator\":\"transition\",\"to\":\"Ready\"}")]
    [InlineData("itemUpdated", "{\"target\":\"tags\",\"operator\":\"contains\",\"term\":\"x\",\"includeDescendants\":\"yes\"}")]
    [InlineData("itemUpdated", "{\"target\":\"tags\",\"operator\":\"added\",\"unknown\":true}")]
    public void Invalid_conditions_are_rejected(string trigger, string json) => Assert.NotEmpty(TriggerConditions.Validate(new WorkflowTrigger(trigger, Parameters: new(Node(json)))));

    [Fact]
    public void Condition_trees_and_operands_are_bounded()
    {
        var node = Node("""{"target":"field","field":"value","operator":"isEmpty"}""");
        for (var i = 0; i < 8; i++) node = new JsonObject { ["all"] = new JsonArray(node) };
        Assert.NotEmpty(TriggerConditions.Validate(new WorkflowTrigger("itemUpdated", Parameters: new(node))));
        node = new JsonObject { ["all"] = new JsonArray(Enumerable.Range(0, 100).Select(_ => (JsonNode)Node("""{"target":"tags","operator":"added"}""")).ToArray()) };
        Assert.NotEmpty(TriggerConditions.Validate(new WorkflowTrigger("itemUpdated", Parameters: new(node))));
        node = Node("""{"target":"field","field":"value","operator":"containsAny"}""");
        node["value"] = new JsonArray(Enumerable.Range(0, 101).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        Assert.NotEmpty(TriggerConditions.Validate(new WorkflowTrigger("itemUpdated", Parameters: new(node))));
    }

    private sealed class Terms : ITermStore
    {
        public int Lookups { get; private set; }
        public Task<Guid?> FindTermByPathAsync(string path, CancellationToken cancellationToken) { Lookups++; return Task.FromResult<Guid?>(Tag); }
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetDescendantsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>>(new Dictionary<Guid, IReadOnlyList<Guid>> { [Tag] = [Tag, Child] });
        public Task<TermSetInfo?> GetTermSetAsync(Guid termSetId, CancellationToken cancellationToken) => Task.FromResult<TermSetInfo?>(null);
        public Task<string?> GetTermSetPathAsync(Guid termSetId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<Guid?> FindTermSetAsync(string groupName, string setName, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<TermSetInfo> GetKeywordsSetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> ResolveAsync(Guid termSetId, string value, bool allowCreate, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetLabelsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>(new Dictionary<Guid, IReadOnlyList<string>>());
        public Task<IReadOnlyList<TermInfo>> ListTermsAsync(Guid termSetId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TermInfo>>([]);
        public Task<IReadOnlyList<TermInfo>> GetTermsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TermInfo>>([]);
        public Task<IReadOnlyDictionary<Guid, string>> GetTermPathsAsync(IReadOnlyCollection<Guid> termIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
}
