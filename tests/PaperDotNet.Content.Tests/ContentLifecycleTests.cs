using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PaperDotNet.Content.Tests;

public sealed class ContentLifecycleTests(ContentFixture fixture) : IClassFixture<ContentFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string TypeId = "11111111-1111-4111-8111-111111111111";
    private static readonly EffectiveSchema BasicSchema = new([], [new SchemaContentType(TypeId, "item", "Item", [], true)]);
    private static ContentInput Input(string name, string values = "{}") => new(name, TypeId, JsonSerializer.Deserialize<JsonElement>(values));

    private static async Task<(ContentStore Store, ResourcesRow Collection)> SetupAsync(ContentDbContext db, bool publishing = true, string history = "major", bool library = false, long maxVersions = 50)
    {
        var store = new ContentStore(db, new TestAuthorization());
        var workspace = await store.CreateWorkspaceAsync("admin", "Workspace", cancellationToken: Ct);
        var collection = await store.CreateCollectionAsync("admin", workspace.Id, "Collection", library, publishing, history, maxVersions, cancellationToken: Ct);
        await store.RecordSchemaAsync("admin", collection.Id, collection.Version, BasicSchema, cancellationToken: Ct);
        return (store, collection);
    }

    [Fact]
    public async Task Collection_and_initial_schema_commit_together_or_roll_back_together()
    {
        await using var db = await fixture.OpenAsync();
        var store = new ContentStore(db, new TestAuthorization());
        var workspace = await store.CreateWorkspaceAsync("admin", "Workspace", Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateCollectionAsync("admin", workspace.Id, "Invalid", new EffectiveSchema([], []), cancellationToken: Ct));
        Assert.Equal(1, await db.Resources.CountAsync(Ct));
        Assert.Empty(await db.SchemaRevisions.ToListAsync(Ct));
        var collection = await store.CreateCollectionAsync("admin", workspace.Id, "Valid", BasicSchema, cancellationToken: Ct);
        Assert.NotNull(collection.SchemaHeadId);
        Assert.Equal(1, collection.Version);
        Assert.Equal(collection.Id, (await db.SchemaRevisions.SingleAsync(Ct)).ContainerId);
    }

    [Fact]
    public async Task Drafts_publish_edits_unpublish_and_republish_keep_independent_surfaces()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db);
        var item = await store.CreateItemAsync("editor", collection.Id, Input("First"), cancellationToken: Ct);
        Assert.Null(item.PublishedRevisionId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadAsync("reader", item.Id, cancellationToken: Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadAsync("reader", item.Id, "head", cancellationToken: Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.PublishAsync("editor", item.Id, item.Version, cancellationToken: Ct));
        await store.PublishAsync("publisher", item.Id, item.Version, cancellationToken: Ct);
        item = await db.Resources.AsNoTracking().SingleAsync(x => x.Id == item.Id, Ct);
        Assert.Equal(2, item.Version);
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Second"), cancellationToken: Ct);
        Assert.Equal("First", (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Revision.Name);
        var visible = await store.ReadAsync("reader", item.Id, cancellationToken: Ct);
        Assert.Equal("First", visible.Resource.Name);
        Assert.Null(visible.Resource.HeadRevisionId);
        Assert.Equal("Second", (await store.ReadAsync("editor", item.Id, "head", cancellationToken: Ct)).Revision.Name);
        item = await store.UnpublishAsync("publisher", item.Id, item.Version, cancellationToken: Ct);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadAsync("reader", item.Id, cancellationToken: Ct));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => store.PublishAsync("publisher", item.Id, item.Version - 1, cancellationToken: Ct));
        await store.PublishAsync("publisher", item.Id, item.Version, cancellationToken: Ct);
        Assert.Equal("Second", (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Revision.Name);
        Assert.Equal(5, (await db.Resources.AsNoTracking().SingleAsync(x => x.Id == item.Id, cancellationToken: Ct)).Version);
        Assert.Equal(2, await db.ItemRevisions.CountAsync(cancellationToken: Ct));
        Assert.Equal(2, await db.ItemHistories.CountAsync(cancellationToken: Ct));
        Assert.Equal(3, await db.Publications.CountAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Archive_is_idempotent_preserves_snapshots_content_stamps_and_name_and_filters_before_limit()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db, publishing: false);
        var item = await store.CreateItemAsync("editor", collection.Id, Input("Archive"), cancellationToken: Ct);
        var live = await store.CreateItemAsync("editor", collection.Id, Input("Visible"), cancellationToken: Ct);
        var head = item.HeadRevisionId; var published = item.PublishedRevisionId; var modified = item.UpdatedAt; var editor = item.UpdatedBy;
        var events = await db.Set<DomainEventsRow>().CountAsync(cancellationToken: Ct);
        item = await store.SetArchivedAsync("editor", item.Id, item.Version, true, cancellationToken: Ct);
        Assert.NotNull(item.ArchivedAt);
        Assert.Equal("editor", item.ArchivedBy);
        Assert.Equal(head, item.HeadRevisionId);
        Assert.Equal(published, item.PublishedRevisionId);
        Assert.Equal(modified, item.UpdatedAt);
        Assert.Equal(editor, item.UpdatedBy);
        Assert.Equal(live.Id, Assert.Single(await store.BrowseAsync("reader", collection.Id, limit: 1, cancellationToken: Ct)).Resource.Id);
        Assert.Equal(item.Id, Assert.Single(await store.BrowseAsync("reader", collection.Id, archive: ArchiveFilter.Archived, cancellationToken: Ct)).Resource.Id);
        Assert.Equal(2, (await store.BrowseAsync("reader", collection.Id, archive: ArchiveFilter.All, cancellationToken: Ct)).Count);
        Assert.Equal("Archive", (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Revision.Name);
        var version = item.Version;
        await store.SetArchivedAsync("editor", item.Id, version, true, cancellationToken: Ct);
        Assert.Equal(events + 1, await db.Set<DomainEventsRow>().CountAsync(cancellationToken: Ct));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => store.SetArchivedAsync("editor", item.Id, version - 1, true, cancellationToken: Ct));
        item = await store.SetArchivedAsync("editor", item.Id, version, false, cancellationToken: Ct);
        Assert.Equal(version + 1, item.Version);
        Assert.Null(item.ArchivedAt); Assert.Null(item.ArchivedBy);
        Assert.Equal(2, await db.ItemRevisions.CountAsync(cancellationToken: Ct));
        Assert.Equal(2, await db.Publications.CountAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Saved_draft_publishes_with_frozen_schema_after_required_field_is_added()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db);
        var item = await store.CreateItemAsync("editor", collection.Id, Input("Old draft"), cancellationToken: Ct);
        var original = (await store.ReadAsync("editor", item.Id, "head", cancellationToken: Ct)).Schema;
        var changed = new EffectiveSchema([new SchemaField("required", "Required", "text", true)],
            [new SchemaContentType(TypeId, "item", "Item", ["required"], true)]);
        await store.RecordSchemaAsync("admin", collection.Id, collection.Version, changed, cancellationToken: Ct);
        await store.PublishAsync("publisher", item.Id, item.Version, cancellationToken: Ct);
        Assert.Equal(original.Id, (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Schema.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateItemAsync("editor", collection.Id, Input("Invalid"), cancellationToken: Ct));
        Assert.Equal(2, await db.SchemaRevisions.CountAsync(cancellationToken: Ct));
        Assert.Equal(1, await db.ItemRevisions.CountAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Revision_and_publication_updates_and_deletes_are_rejected_by_EF()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db, publishing: false);
        var item = await store.CreateItemAsync("editor", collection.Id, Input("Original"), cancellationToken: Ct);
        var revision = await db.ItemRevisions.SingleAsync(cancellationToken: Ct);
        revision.Name = "Tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(cancellationToken: Ct));
        db.ChangeTracker.Clear();
        var schema = await db.SchemaRevisions.SingleAsync(cancellationToken: Ct);
        db.SchemaRevisions.Remove(schema);
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();
        var publication = await db.Publications.SingleAsync(cancellationToken: Ct);
        publication.Snapshot = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(cancellationToken: Ct));
        db.ChangeTracker.Clear();
        Assert.Equal("Original", (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Revision.Name);
    }

    [Fact]
    public async Task Automatic_publishing_has_one_surface_and_history_is_independent_and_bounded()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db, publishing: false, history: "off", maxVersions: 2);
        var item = await store.CreateItemAsync("editor", collection.Id, Input("One"), cancellationToken: Ct);
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Two"), cancellationToken: Ct);
        Assert.Empty(await db.ItemHistories.ToListAsync(cancellationToken: Ct));
        Assert.Equal(2, await db.ItemRevisions.CountAsync(cancellationToken: Ct));
        Assert.Equal("published", (await db.ItemSurfaces.SingleAsync(cancellationToken: Ct)).Surface);
        Assert.Equal(item.HeadRevisionId, item.PublishedRevisionId);
        collection = await store.ConfigureCollectionAsync("admin", collection.Id, collection.Version, true, "major", 2, cancellationToken: Ct);
        Assert.Equal(2, await db.ItemSurfaces.CountAsync(cancellationToken: Ct));
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Three"), cancellationToken: Ct);
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Four"), cancellationToken: Ct);
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Five"), cancellationToken: Ct);
        Assert.Equal(new long[] { 2, 3 }, await db.ItemHistories.OrderBy(x => x.Number).Select(x => x.Number).ToArrayAsync(cancellationToken: Ct));
        Assert.Equal("Two", (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Revision.Name);
        await store.ConfigureCollectionAsync("admin", collection.Id, collection.Version, false, "off", 2, cancellationToken: Ct);
        Assert.Equal("Five", (await store.ReadAsync("reader", item.Id, cancellationToken: Ct)).Revision.Name);
        Assert.Equal(2, await db.ItemHistories.CountAsync(cancellationToken: Ct));
        Assert.Equal("published", (await db.ItemSurfaces.SingleAsync(cancellationToken: Ct)).Surface);
    }

    [Fact]
    public async Task Folders_are_immediately_visible_without_publications_or_history_and_cannot_be_archived()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db);
        var folder = await store.CreateItemAsync("editor", collection.Id, Input("Folder"), folder: true, cancellationToken: Ct);
        Assert.Equal("Folder", (await store.ReadAsync("reader", folder.Id, cancellationToken: Ct)).Revision.Name);
        Assert.Empty(await db.Publications.ToListAsync(cancellationToken: Ct)); Assert.Empty(await db.ItemHistories.ToListAsync(cancellationToken: Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetArchivedAsync("editor", folder.Id, folder.Version, true, cancellationToken: Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync("publisher", folder.Id, folder.Version, cancellationToken: Ct));
    }

    [Fact]
    public async Task Restoring_history_creates_a_new_revision_and_retains_the_original_snapshots()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db, publishing: false);
        var item = await store.CreateItemAsync("editor", collection.Id, Input("First"), cancellationToken: Ct);
        var firstRevision = item.HeadRevisionId;
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Second"), cancellationToken: Ct);
        item = await store.RestoreHistoryAsync("editor", item.Id, item.Version, 1, Ct);
        Assert.Equal("First", item.Name);
        Assert.NotEqual(firstRevision, item.HeadRevisionId);
        Assert.Equal("First", (await db.ItemRevisions.AsNoTracking().SingleAsync(x => x.Id == firstRevision, Ct)).Name);
        var history = await store.HistoryAsync("reader", item.Id, cancellationToken: Ct);
        Assert.Equal(new long[] { 3, 2, 1 }, history.Select(x => x.Number).ToArray());
        Assert.Equal(new string[] { "First", "Second", "First" }, history.Select(x => x.Revision.Name).ToArray());
        Assert.All(history, x => Assert.Equal(collection.Id, x.Schema.ContainerId));
        Assert.Equal(3, await db.ItemRevisions.CountAsync(Ct));
        Assert.Equal(1, await db.Set<AuditEventsRow>().CountAsync(x => x.Action == "item.restore_revision", Ct));
    }

    [Fact]
    public async Task Library_publications_pin_blob_and_filename_and_archived_names_remain_reserved()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db, library: true);
        var item = await store.CreateItemAsync("editor", collection.Id, Input("Original.pdf"), cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync("publisher", item.Id, item.Version, Ct));
        var blobId = Guid.NewGuid().ToString();
        db.Set<BlobsRow>().Add(new BlobsRow
        {
            Id = blobId,
            ItemId = item.Id,
            Version = 1,
            ObjectKey = "test-blob",
            Filename = "Original.pdf",
            ContentType = "application/pdf",
            Size = 1,
            Sha256 = new string('a', 64)
        });
        await db.SaveChangesAsync(Ct);
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Original.pdf") with { BlobId = blobId }, Ct);
        await store.PublishAsync("publisher", item.Id, item.Version, Ct);
        item = await db.Resources.AsNoTracking().SingleAsync(x => x.Id == item.Id, Ct);
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Renamed"), Ct);
        var published = await store.ReadAsync("reader", item.Id, cancellationToken: Ct);
        var head = await store.ReadAsync("editor", item.Id, "head", Ct);
        Assert.Equal("Original.pdf", published.Revision.Filename);
        Assert.Equal("Renamed.pdf", head.Revision.Filename);
        Assert.Equal(blobId, head.Revision.BlobId);
        Assert.Equal(blobId, published.Revision.BlobId);
        item = await store.SetArchivedAsync("editor", item.Id, item.Version, true, Ct);
        Assert.Equal("renamed", item.NameKey);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateItemAsync("editor", collection.Id, Input("Renamed"), cancellationToken: Ct));
        Assert.Equal(1, await db.Resources.CountAsync(x => x.Kind == "item", Ct));
        Assert.Equal(1, await db.Publications.CountAsync(Ct));
    }

    [Fact]
    public async Task Defaults_and_cross_field_rules_are_frozen_and_enforced_before_a_revision_is_committed()
    {
        await using var db = await fixture.OpenAsync();
        var (store, collection) = await SetupAsync(db);
        var schema = new EffectiveSchema([
            new SchemaField("approved", "Approved", "boolean", Options: new SchemaFieldOptions(DefaultValue: JsonSerializer.SerializeToElement(false))),
            new SchemaField("reason", "Reason", "text")],
            [new SchemaContentType(TypeId, "item", "Item", ["approved", "reason"], true,
                [new SchemaRule("needs_reason", "reason", "required_if", "Approval needs a reason", WhenField: "approved", WhenValue: JsonSerializer.SerializeToElement(true))])]);
        await store.RecordSchemaAsync("admin", collection.Id, collection.Version, schema, Ct);
        var item = await store.CreateItemAsync("editor", collection.Id, Input("Pending"), cancellationToken: Ct);
        Assert.False(JsonSerializer.Deserialize<JsonElement>(item.Values).GetProperty("approved").GetBoolean());
        await Assert.ThrowsAsync<ArgumentException>(() => store.EditAsync("editor", item.Id, item.Version, Input("Invalid", "{\"approved\":true}"), Ct));
        Assert.Equal(1, await db.ItemRevisions.CountAsync(Ct));
        item = await store.EditAsync("editor", item.Id, item.Version, Input("Approved", "{\"approved\":true,\"reason\":\"Reviewed\"}"), Ct);
        Assert.Equal(2, await db.ItemRevisions.CountAsync(Ct));
        Assert.Equal("Approved", item.Name);
    }

    private sealed class TestAuthorization : IContentAuthorization
    {
        public Task AuthorizeAsync(string actor, ResourcesRow resource, string action, CancellationToken cancellationToken)
        {
            if (actor == "admin" || actor == "reader" && action == "read" || actor == "editor" && action is "read" or "read_draft" or "write"
                || actor == "publisher" && action is "read" or "read_draft" or "publish") return Task.CompletedTask;
            throw new UnauthorizedAccessException("Action denied.");
        }
    }
}
