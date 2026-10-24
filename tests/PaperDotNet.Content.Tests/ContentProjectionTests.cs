using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PaperDotNet.Content.Tests;

public sealed class ContentProjectionTests(ContentFixture fixture) : IClassFixture<ContentFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string TypeId = Guid.NewGuid().ToString();
    private static ContentInput Input(string name, string values) => new(name, TypeId, JsonSerializer.Deserialize<JsonElement>(values));
    private static EffectiveSchema Schema => new([
        new SchemaField("serial", "Serial", "integer", Indexed: true, Options: new SchemaFieldOptions(Unique: true)),
        new SchemaField("amount", "Amount", "decimal", Indexed: true, Scale: 2),
        new SchemaField("number", "Number", "number", Indexed: true),
        new SchemaField("when", "When", "datetime", Indexed: true),
        new SchemaField("status", "Status", "choice", Choices: ["Open", "Closed"], Indexed: true),
        ], [new SchemaContentType(TypeId, "record", "Record", ["serial", "amount", "number", "when", "status"], true)]);

    [Fact]
    public async Task Typed_projections_preserve_exact_numbers_timestamps_counts_and_keys_on_both_surfaces()
    {
        await using var db = await fixture.OpenAsync();
        var store = new ContentStore(db, new AllowAll());
        var workspace = await store.CreateWorkspaceAsync("admin", "Workspace", Ct);
        var collection = await store.CreateCollectionAsync("admin", workspace.Id, "List", publishingEnabled: true, cancellationToken: Ct);
        await store.RecordSchemaAsync("admin", collection.Id, collection.Version, Schema, Ct);
        var item = await store.CreateItemAsync("admin", collection.Id, Input("First", """
            {"serial":9223372036854775807,"amount":"12345678901234567.89","number":79228162514264337593543950335,"when":"2026-10-23T11:20:30.123456789+02:00","status":"Open"}
            """), cancellationToken: Ct);
        var values = await db.Set<FieldValuesRow>().ToDictionaryAsync(x => x.FieldKey, Ct);
        Assert.Equal(long.MaxValue, values["serial"].ValueInteger);
        Assert.Equal(1234567890123456789, values["amount"].ValueInteger);
        Assert.Equal("2026-10-23T09:20:30.123456789Z", values["when"].ValueText);
        Assert.Equal("1" + "79228162514264337593543950335" + new string('0', 28), values["number"].ValueText);
        Assert.Equal("9223372036854775807", (await db.Set<BusinessKeysRow>().SingleAsync(Ct)).Value);
        Assert.Equal(1, (await db.Set<GroupCountsRow>().SingleAsync(x => x.FieldKey == "status", Ct)).Count);
        await store.PublishAsync("admin", item.Id, item.Version, Ct);
        item = await db.Resources.AsNoTracking().SingleAsync(x => x.Id == item.Id, Ct);
        item = await store.EditAsync("admin", item.Id, item.Version, Input("Draft", """
            {"serial":9223372036854775806,"amount":"0.10","number":-0.1,"when":"2026-10-23T00:00:00Z","status":"Closed"}
            """), Ct);
        Assert.Equal(2, await db.Set<BusinessKeysRow>().CountAsync(Ct));
        Assert.Equal("Open", (await db.Set<GroupCountsRow>().SingleAsync(x => x.Surface == "published" && x.FieldKey == "status", Ct)).Value);
        Assert.Equal("Closed", (await db.Set<GroupCountsRow>().SingleAsync(x => x.Surface == "head" && x.FieldKey == "status", Ct)).Value);
        var revisionCount = await db.ItemRevisions.CountAsync(Ct);
        // The old published key remains reserved despite the draft's new key. Failure rolls back inserted resource/revision/projections.
        await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateItemAsync("admin", collection.Id,
            Input("Duplicate", "{\"serial\":9223372036854775807}"), cancellationToken: Ct));
        Assert.Equal(revisionCount, await db.ItemRevisions.CountAsync(Ct));
        Assert.Equal(1, await db.Resources.CountAsync(x => x.Kind == "item", Ct));
        Assert.Equal(10, await db.Set<FieldValuesRow>().CountAsync(Ct));
        await store.UnpublishAsync("admin", item.Id, item.Version, Ct);
        Assert.Equal("9223372036854775806", (await db.Set<BusinessKeysRow>().SingleAsync(Ct)).Value);
        Assert.Equal(2, await db.Set<GroupCountsRow>().CountAsync(Ct));
    }

    [Fact]
    public async Task Keywords_are_projected_by_term_id_and_invalid_or_duplicate_keywords_rollback()
    {
        await using var db = await fixture.OpenAsync();
        var store = new ContentStore(db, new AllowAll());
        var workspace = await store.CreateWorkspaceAsync("admin", "Workspace", Ct);
        var collection = await store.CreateCollectionAsync("admin", workspace.Id, "List", cancellationToken: Ct);
        await store.RecordSchemaAsync("admin", collection.Id, collection.Version, new EffectiveSchema([], [new SchemaContentType(TypeId, "record", "Record", [], true)]), Ct);
        var keyword = Guid.NewGuid().ToString();
        db.Set<TermsRow>().Add(new TermsRow { Id = keyword, TermSetId = "00000000-0000-4000-8000-000000000002", Name = "Invoice", NormalizedName = "invoice", Path = "/" + keyword + "/", AvailableAsKeyword = true });
        await db.SaveChangesAsync(Ct);
        var item = await store.CreateItemAsync("admin", collection.Id, Input("Tagged", "{}") with { Keywords = [keyword] }, cancellationToken: Ct);
        Assert.Equal(keyword, (await db.Set<ResourceKeywordsRow>().SingleAsync(Ct)).Keyword);
        Assert.Equal(keyword, (await db.Set<ItemSurfaceKeywordsRow>().SingleAsync(Ct)).Keyword);
        await Assert.ThrowsAsync<ArgumentException>(() => store.EditAsync("admin", item.Id, item.Version,
            Input("Invalid", "{}") with { Keywords = [keyword, keyword] }, Ct));
        Assert.Equal("Tagged", (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Revision.Name);
        Assert.Single(await db.ItemRevisions.ToListAsync(Ct));
    }

    [Fact]
    public async Task Independent_contexts_reject_stale_versions_and_cancelled_commands_leave_no_partial_rows()
    {
        await using var db = await fixture.OpenAsync();
        var store = new ContentStore(db, new AllowAll());
        var workspace = await store.CreateWorkspaceAsync("admin", "Workspace", Ct);
        var collection = await store.CreateCollectionAsync("admin", workspace.Id, "List", cancellationToken: Ct);
        await store.RecordSchemaAsync("admin", collection.Id, collection.Version, new EffectiveSchema([], [new SchemaContentType(TypeId, "record", "Record", [], true)]), Ct);
        var item = await store.CreateItemAsync("admin", collection.Id, Input("Initial", "{}"), cancellationToken: Ct);
        await using ContentDbContext other = db.Database.ProviderName!.Contains("Npgsql", StringComparison.Ordinal)
            ? Persistence.PostgreSql.Content.PostgreSqlContentDbContext.Open(db.Database.GetConnectionString()!)
            : Persistence.Sqlite.Content.SqliteContentDbContext.Open(db.Database.GetConnectionString()!);
        var second = new ContentStore(other, new AllowAll());
        var current = await second.EditAsync("admin", item.Id, item.Version, Input("Winner", "{}"), Ct);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => store.EditAsync("admin", item.Id, item.Version, Input("Lost", "{}"), Ct));
        Assert.Equal("Winner", (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Revision.Name);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.SetArchivedAsync("admin", current.Id, current.Version, true, cancelled.Token));
        Assert.Equal(2, await db.ItemRevisions.CountAsync(Ct));
        Assert.Null((await db.Resources.AsNoTracking().SingleAsync(x => x.Id == item.Id, Ct)).ArchivedAt);
    }

    [Fact]
    public async Task Enabling_an_index_rebuilds_existing_snapshots_and_choice_counts_without_mutating_revisions()
    {
        await using var db = await fixture.OpenAsync();
        var store = new ContentStore(db, new AllowAll());
        var workspace = await store.CreateWorkspaceAsync("admin", "Workspace", Ct);
        var collection = await store.CreateCollectionAsync("admin", workspace.Id, "List", cancellationToken: Ct);
        var schema = new EffectiveSchema([new SchemaField("status", "Original label", "choice", Choices: ["Open", "Closed"])],
            [new SchemaContentType(TypeId, "record", "Record", ["status"], true)]);
        var original = await store.RecordSchemaAsync("admin", collection.Id, collection.Version, schema, Ct);
        var item = await store.CreateItemAsync("admin", collection.Id, Input("Existing", "{\"status\":\"Open\"}"), cancellationToken: Ct);
        var revisionId = item.HeadRevisionId;
        Assert.Equal(0, await db.Set<FieldValuesRow>().CountAsync(Ct));
        schema = JsonSerializer.Deserialize<EffectiveSchema>(original.Definition)!;
        schema = schema with { Fields = [schema.Fields[0] with { Label = "New label", Indexed = true }] };
        await store.RecordSchemaAsync("admin", collection.Id, collection.Version, schema, Ct);
        Assert.Equal("Open", (await db.Set<FieldValuesRow>().SingleAsync(Ct)).ValueText);
        Assert.Equal(1, (await db.Set<GroupCountsRow>().SingleAsync(x => x.FieldKey == "status", Ct)).Count);
        var visible = await store.ReadAsync("reader", item.Id, cancellationToken: Ct);
        Assert.Equal(revisionId, visible.Revision.Id);
        Assert.Equal(original.Id, visible.Schema.Id);
        Assert.Equal("Original label", JsonSerializer.Deserialize<EffectiveSchema>(visible.Schema.Definition)!.Fields[0].Label);
        Assert.Equal(1, await db.ItemRevisions.CountAsync(Ct));
    }

    [Fact]
    public async Task Collection_schema_commands_cannot_silently_change_a_shared_catalog_for_other_collections()
    {
        await using var db = await fixture.OpenAsync();
        var store = new ContentStore(db, new AllowAll());
        var workspace = await store.CreateWorkspaceAsync("admin", "Workspace", Ct);
        var first = await store.CreateCollectionAsync("admin", workspace.Id, "First", cancellationToken: Ct);
        var second = await store.CreateCollectionAsync("admin", workspace.Id, "Second", cancellationToken: Ct);
        var original = await store.RecordSchemaAsync("admin", first.Id, first.Version, Schema, Ct);
        var definition = JsonSerializer.Deserialize<EffectiveSchema>(original.Definition)!;
        await store.RecordSchemaAsync("admin", second.Id, second.Version, definition, Ct);
        definition = definition with { Fields = definition.Fields.Select(x => x with { Label = "Changed" }).ToArray() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordSchemaAsync("admin", first.Id, first.Version, definition, Ct));
        Assert.Equal(2, await db.SchemaRevisions.CountAsync(Ct));
        Assert.DoesNotContain(await db.Set<FieldDefinitionsRow>().ToListAsync(Ct), x => x.Label == "Changed");
    }

    private sealed class AllowAll : IContentAuthorization
    {
        public Task AuthorizeAsync(string actor, ResourcesRow resource, string action, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
