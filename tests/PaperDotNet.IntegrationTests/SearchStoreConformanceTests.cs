using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Search.Contracts;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// What every search store must do (ADR-0043, docs/search-zvec-plan.md WP1): the same tests run against each store
/// through <see cref="ISearchStore"/> only, with synthetic documents in a fresh tenant. A store that cannot run here
/// (e.g. without its native library) skips with the reason from <see cref="SkipReason"/>.
/// </summary>
public abstract class SearchStoreConformanceTests(PaperDotNetApiFactory factory)
{
    private const string Model = "conformance:test:4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The <c>Search:Store</c> name of the store under test.</summary>
    protected abstract string StoreName { get; }

    /// <summary>Why the store cannot be tested in this environment, or null.</summary>
    protected virtual string? SkipReason => null;

    private sealed class Context(PaperDotNetApiFactory factory, TenantSummary tenant, string storeName)
    {
        public TenantSummary Tenant => tenant;

        public async Task<T> RunAsync<T>(Func<ISearchStore, Task<T>> action)
        {
            await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
            return await action(scope.ServiceProvider.GetRequiredKeyedService<ISearchStore>(storeName));
        }

        public async Task RunAsync(Func<ISearchStore, Task> action)
        {
            await using var scope = factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(tenant.Id, tenant.Identifier);
            await action(scope.ServiceProvider.GetRequiredKeyedService<ISearchStore>(storeName));
        }
    }

    private async Task<Context> NewTenantAsync()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);
        var tenant = await factory.CreateTenantAsync($"store-{StoreName}-{Guid.NewGuid():N}"[..40]);
        return new Context(factory, tenant, StoreName);
    }

    private static SearchDocumentData Document(
        Guid scope, string title, string body = "", Guid? workspace = null, Guid? container = null, IReadOnlyList<string>? pages = null,
        IReadOnlyCollection<Guid>? terms = null, DateTimeOffset? updated = null, string source = "test", Guid? contentType = null, Guid? author = null) =>
        new(PaperDotNet.Abstractions.Ids.New(), source, workspace ?? Guid.Empty, container, contentType, title, body, scope, terms ?? [], author, updated ?? DateTimeOffset.UnixEpoch)
        {
            Pages = pages ?? [],
        };

    private static StoreSearchQuery Keyword(string? text, StoreFilter filter, int top = 25, int skip = 0) =>
        new(SearchMode.Keyword, text is null ? null : FullTextQuery.Parse(text, out _), filter, skip, top) { WithFacets = true };

    private static StoreFilter Readable(params Guid[] scopes) => new(scopes);

    private static List<Guid> Ids(StoreSearchResult result) => result.Hits.Select(h => h.Id).ToList();

    [Fact]
    public async Task Keyword_search_finds_title_body_keywords_and_pages()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var byTitle = Document(scope, "Quarterly zephyr report");
        var byBody = Document(scope, "Notes", "the zephyr was strong");
        var byKeywords = Document(scope, "Tagged") with { Keywords = "zephyr" };
        var byPage = Document(scope, "Scan", pages: ["nothing here", "a zephyr on page two"]);
        var other = Document(scope, "Unrelated", "calm weather");
        await ctx.RunAsync(s => s.UpsertAsync([byTitle, byBody, byKeywords, byPage, other], Ct));

        var result = await ctx.RunAsync(s => s.SearchAsync(Keyword("zephyr", Readable(scope)), Ct));

        Assert.Equal(4, result.Count);
        Assert.Equivalent(new[] { byTitle.Id, byBody.Id, byKeywords.Id, byPage.Id }, Ids(result));
        Assert.All(result.Hits, h => Assert.Equal([SearchMatch.Keyword], h.MatchedBy));
        var pageHit = result.Hits.Single(h => h.Id == byPage.Id);
        Assert.Equal(2, pageHit.Page);
        Assert.Contains("zephyr", pageHit.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Title_matches_rank_above_body_matches()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var body = Document(scope, "Minutes", "we discussed the quokka budget at length");
        var title = Document(scope, "Quokka budget", "figures for next year");
        await ctx.RunAsync(s => s.UpsertAsync([body, title], Ct));

        var result = await ctx.RunAsync(s => s.SearchAsync(Keyword("quokka", Readable(scope)), Ct));

        Assert.Equal([title.Id, body.Id], Ids(result));
    }

    [Fact]
    public async Task Query_syntax_phrases_or_exclusions_and_prefixes()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var a = Document(scope, "A", "red apple pie");
        var b = Document(scope, "B", "apple red pie");
        var c = Document(scope, "C", "green pear tart");
        await ctx.RunAsync(s => s.UpsertAsync([a, b, c], Ct));

        async Task<List<Guid>> Find(string q) => Ids(await ctx.RunAsync(s => s.SearchAsync(Keyword(q, Readable(scope)), Ct)));

        Assert.Equal([a.Id], await Find("\"red apple\""));
        Assert.Equivalent(new[] { a.Id, c.Id }, await Find("\"red apple\" OR pear"));
        Assert.Equal([c.Id], await Find("pie OR tart -apple"));
        Assert.Equivalent(new[] { a.Id, b.Id }, await Find("appl*"));
    }

    [Fact]
    public async Task Results_are_trimmed_to_the_readable_scopes()
    {
        var ctx = await NewTenantAsync();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var visible = Document(mine, "Walrus plan");
        var hidden = Document(theirs, "Walrus secret");
        await ctx.RunAsync(s => s.UpsertAsync([visible, hidden], Ct));

        var result = await ctx.RunAsync(s => s.SearchAsync(Keyword("walrus", Readable(mine)), Ct));
        Assert.Equal([visible.Id], Ids(result));
        Assert.Equal(1, result.Count);

        var none = await ctx.RunAsync(s => s.SearchAsync(Keyword("walrus", Readable()), Ct));
        Assert.Empty(none.Hits);
    }

    [Fact]
    public async Task Documents_of_another_tenant_are_never_found()
    {
        var first = await NewTenantAsync();
        var second = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var document = Document(scope, "Narwhal ledger");
        await first.RunAsync(s => s.UpsertAsync([document], Ct));

        var other = await second.RunAsync(s => s.SearchAsync(Keyword("narwhal", Readable(scope)), Ct));
        Assert.Empty(other.Hits);
        await second.RunAsync(s => s.DeleteAsync([document.Id], Ct));

        var own = await first.RunAsync(s => s.SearchAsync(Keyword("narwhal", Readable(scope)), Ct));
        Assert.Equal([document.Id], Ids(own));
    }

    [Fact]
    public async Task Filters_narrow_the_results()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var (ws1, ws2, list, type, author, term) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var jan = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var mar = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var d1 = Document(scope, "Ibis one", workspace: ws1, container: list, contentType: type, author: author, terms: [term], updated: jan);
        var d2 = Document(scope, "Ibis two", workspace: ws1, updated: mar);
        var d3 = Document(scope, "Ibis three", workspace: ws2, updated: mar);
        await ctx.RunAsync(s => s.UpsertAsync([d1, d2, d3], Ct));

        async Task<List<Guid>> Find(StoreFilter filter) => Ids(await ctx.RunAsync(s => s.SearchAsync(Keyword("ibis", filter), Ct)));
        var all = Readable(scope);

        Assert.Equivalent(new[] { d1.Id, d2.Id }, await Find(all with { WorkspaceId = ws1 }));
        Assert.Equal([d1.Id], await Find(all with { ContainerId = list }));
        Assert.Equal([d1.Id], await Find(all with { ContentTypeId = type }));
        Assert.Equal([d1.Id], await Find(all with { CreatedBy = author }));
        Assert.Equal([d1.Id], await Find(all with { TermIds = [Guid.NewGuid(), term] }));
        Assert.Equivalent(new[] { d2.Id, d3.Id }, await Find(all with { UpdatedFrom = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) }));
        Assert.Equal([d1.Id], await Find(all with { UpdatedTo = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) }));
    }

    [Fact]
    public async Task Filters_alone_list_documents_newest_first()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var older = Document(scope, "Older", workspace: ws, updated: DateTimeOffset.UnixEpoch.AddDays(1));
        var newer = Document(scope, "Newer", workspace: ws, updated: DateTimeOffset.UnixEpoch.AddDays(2));
        var elsewhere = Document(scope, "Elsewhere", updated: DateTimeOffset.UnixEpoch.AddDays(3));
        await ctx.RunAsync(s => s.UpsertAsync([older, newer, elsewhere], Ct));

        var result = await ctx.RunAsync(s => s.SearchAsync(Keyword(null, Readable(scope) with { WorkspaceId = ws }), Ct));

        Assert.Equal([newer.Id, older.Id], Ids(result));
        Assert.All(result.Hits, h => Assert.Empty(h.MatchedBy));
    }

    [Fact]
    public async Task Paging_and_counts_cover_every_match()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var documents = Enumerable.Range(0, 7).Select(i => Document(scope, $"Okapi {i}")).ToList();
        await ctx.RunAsync(s => s.UpsertAsync(documents, Ct));

        var first = await ctx.RunAsync(s => s.SearchAsync(Keyword("okapi", Readable(scope), top: 3), Ct));
        var last = await ctx.RunAsync(s => s.SearchAsync(Keyword("okapi", Readable(scope), top: 3, skip: 6), Ct));

        Assert.Equal(7, first.Count);
        Assert.Equal(3, first.Hits.Count);
        Assert.Single(last.Hits);
        Assert.Empty(Ids(first).Intersect(Ids(last)));
    }

    [Fact]
    public async Task Facets_count_workspaces_lists_content_types_and_terms()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var (ws, list, type, term) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ctx.RunAsync(s => s.UpsertAsync(
        [
            Document(scope, "Tapir a", workspace: ws, container: list, contentType: type, terms: [term]),
            Document(scope, "Tapir b", workspace: ws, container: list, terms: [term]),
            Document(scope, "Tapir c", workspace: ws),
        ], Ct));

        var store = await ctx.RunAsync(s => Task.FromResult(s.Capabilities));
        Assert.SkipUnless(store.HasFlag(SearchStoreCapabilities.Facets), "The store leaves facets to the search service.");
        var facets = (await ctx.RunAsync(s => s.SearchAsync(Keyword("tapir", Readable(scope)), Ct))).Facets!;

        Assert.Equal([new FacetValue(ws, 3)], facets.Workspace);
        Assert.Equal([new FacetValue(list, 2)], facets.Container);
        Assert.Equal([new FacetValue(type, 1)], facets.ContentType);
        Assert.Equal([new FacetValue(term, 2)], facets.Term);
    }

    [Fact]
    public async Task Upserting_again_replaces_the_text()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var document = Document(scope, "Draft", "the marmot section");
        await ctx.RunAsync(s => s.UpsertAsync([document], Ct));
        await ctx.RunAsync(s => s.UpsertAsync([document with { Body = "the badger section" }], Ct));

        Assert.Empty((await ctx.RunAsync(s => s.SearchAsync(Keyword("marmot", Readable(scope)), Ct))).Hits);
        Assert.Equal([document.Id], Ids(await ctx.RunAsync(s => s.SearchAsync(Keyword("badger", Readable(scope)), Ct))));
    }

    [Fact]
    public async Task Documents_are_deleted_by_id_container_and_source()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var list = Guid.NewGuid();
        var byId = Document(scope, "Gecko id");
        var inList = Document(scope, "Gecko list", container: list);
        var ofSource = Document(scope, "Gecko source", source: "other");
        var kept = Document(scope, "Gecko kept");
        await ctx.RunAsync(s => s.UpsertAsync([byId, inList, ofSource, kept], Ct));

        await ctx.RunAsync(s => s.DeleteAsync([byId.Id], Ct));
        await ctx.RunAsync(s => s.DeleteContainerAsync(list, Ct));
        await ctx.RunAsync(s => s.DeleteSourceAsync("other", Ct));

        Assert.Equal([kept.Id], Ids(await ctx.RunAsync(s => s.SearchAsync(Keyword("gecko", Readable(scope)), Ct))));
    }

    [Fact]
    public async Task Moving_documents_to_another_scope_changes_who_finds_them()
    {
        var ctx = await NewTenantAsync();
        var (before, after) = (Guid.NewGuid(), Guid.NewGuid());
        var document = Document(before, "Lemur memo");
        await ctx.RunAsync(s => s.UpsertAsync([document], Ct));

        await ctx.RunAsync(s => s.SetScopesAsync(new Dictionary<Guid, Guid> { [document.Id] = after, [Guid.NewGuid()] = after }, Ct));

        Assert.Empty((await ctx.RunAsync(s => s.SearchAsync(Keyword("lemur", Readable(before)), Ct))).Hits);
        Assert.Equal([document.Id], Ids(await ctx.RunAsync(s => s.SearchAsync(Keyword("lemur", Readable(after)), Ct))));
    }

    [Fact]
    public async Task Term_usage_is_counted()
    {
        var ctx = await NewTenantAsync();
        var scope = Guid.NewGuid();
        var (used, twice, unused) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ctx.RunAsync(s => s.UpsertAsync([Document(scope, "a", terms: [used, twice]), Document(scope, "b", terms: [twice])], Ct));

        var counts = await ctx.RunAsync(s => s.CountTermsAsync([used, twice, unused], Ct));

        Assert.Equal(new Dictionary<Guid, int> { [used] = 1, [twice] = 2 }, counts);
    }

    /// <summary>Embeds every pending passage with <paramref name="vectorOf"/> (the job's work, without a model).</summary>
    private static async Task EmbedAsync(Context ctx, Func<string, float[]> vectorOf)
    {
        while (true)
        {
            var pending = await ctx.RunAsync(s => s.GetPassagesToEmbedAsync(Model, 100, Ct));
            if (pending.Count == 0)
            {
                return;
            }

            await ctx.RunAsync(s => s.SetEmbeddingsAsync(Model, pending.Select(p => new PassageEmbedding(p.Id, vectorOf(p.Input))).ToList(), Ct));
        }
    }

    /// <summary>A 4-dimensional concept vector: animals, money, weather, other.</summary>
    private static float[] Concepts(string text)
    {
        var lower = text.ToLowerInvariant();
        float[] v =
        [
            lower.Contains("cat", StringComparison.Ordinal) || lower.Contains("dog", StringComparison.Ordinal) ? 1 : 0,
            lower.Contains("invoice", StringComparison.Ordinal) || lower.Contains("payment", StringComparison.Ordinal) ? 1 : 0,
            lower.Contains("rain", StringComparison.Ordinal) ? 1 : 0,
            0.05f,
        ];
        return v;
    }

    private static StoreSearchQuery Semantic(SearchMode mode, string? text, float[] vector, StoreFilter filter) =>
        new(mode, text is null ? null : FullTextQuery.Parse(text, out _), filter, 0, 25)
        {
            Vector = vector,
            VectorModel = Model,
            MinSimilarity = 0.5f,
            CandidateLimit = 200,
            WithFacets = true,
        };

    [Fact]
    public async Task Semantic_search_finds_by_meaning_and_points_to_the_page()
    {
        var ctx = await NewTenantAsync();
        Assert.SkipUnless(await ctx.RunAsync(s => Task.FromResult(s.Capabilities.HasFlag(SearchStoreCapabilities.Vector))), "No vector search.");
        var scope = Guid.NewGuid();
        var pets = Document(scope, "Household", pages: ["shopping list", "the dog and the cat sleep"]);
        var money = Document(scope, "Accounts", "invoice and payment terms");
        var hidden = Document(Guid.NewGuid(), "Pets elsewhere", "a cat");
        await ctx.RunAsync(s => s.UpsertAsync([pets, money, hidden], Ct));
        await EmbedAsync(ctx, Concepts);
        Assert.Empty(await ctx.RunAsync(s => s.GetPassagesToEmbedAsync(Model, 10, Ct)));

        var result = await ctx.RunAsync(s => s.SearchAsync(Semantic(SearchMode.Semantic, "kitten", Concepts("cat"), Readable(scope)), Ct));

        var hit = Assert.Single(result.Hits);
        Assert.Equal(pets.Id, hit.Id);
        Assert.Equal(2, hit.Page);
        Assert.Equal([SearchMatch.Semantic], hit.MatchedBy);
    }

    [Fact]
    public async Task Hybrid_search_fuses_both_sides_and_excluded_words_still_exclude()
    {
        var ctx = await NewTenantAsync();
        Assert.SkipUnless(await ctx.RunAsync(s => Task.FromResult(s.Capabilities.HasFlag(SearchStoreCapabilities.Vector))), "No vector search.");
        var scope = Guid.NewGuid();
        var both = Document(scope, "Payment reminder", "invoice overdue");
        var meaningOnly = Document(scope, "Bill", "payment due");
        var keywordOnly = Document(scope, "Reminder", "water the plants");
        var excluded = Document(scope, "Draft payment", "invoice draft");
        await ctx.RunAsync(s => s.UpsertAsync([both, meaningOnly, keywordOnly, excluded], Ct));
        await EmbedAsync(ctx, Concepts);

        var result = await ctx.RunAsync(s => s.SearchAsync(Semantic(SearchMode.Hybrid, "reminder -draft", Concepts("invoice"), Readable(scope)), Ct));

        Assert.Equal(both.Id, result.Hits[0].Id);
        Assert.Equal([SearchMatch.Keyword, SearchMatch.Semantic], result.Hits[0].MatchedBy);
        Assert.Contains(result.Hits, h => h.Id == meaningOnly.Id && h.MatchedBy.SequenceEqual([SearchMatch.Semantic]));
        Assert.Contains(result.Hits, h => h.Id == keywordOnly.Id && h.MatchedBy.SequenceEqual([SearchMatch.Keyword]));
        Assert.DoesNotContain(result.Hits, h => h.Id == excluded.Id);
    }

    [Fact]
    public async Task Changed_text_is_embedded_again_and_unchanged_text_is_not()
    {
        var ctx = await NewTenantAsync();
        Assert.SkipUnless(await ctx.RunAsync(s => Task.FromResult(s.Capabilities.HasFlag(SearchStoreCapabilities.Vector))), "No vector search.");
        var scope = Guid.NewGuid();
        var document = Document(scope, "Notes", pages: ["first page about rain", "second page about cats"]);
        await ctx.RunAsync(s => s.UpsertAsync([document], Ct));
        await EmbedAsync(ctx, Concepts);

        await ctx.RunAsync(s => s.UpsertAsync([document with { Pages = ["first page about rain", "second page about invoices"] }], Ct));
        var pending = await ctx.RunAsync(s => s.GetPassagesToEmbedAsync(Model, 10, Ct));

        var passage = Assert.Single(pending);
        Assert.Contains("invoices", passage.Input, StringComparison.Ordinal);
    }
}

public sealed class DatabaseSearchStoreConformanceTests(PaperDotNetApiFactory factory) : SearchStoreConformanceTests(factory)
{
    protected override string StoreName => "database";
}
