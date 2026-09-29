using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Features;
using PaperDotNet.Search.Data;
using PaperDotNet.Tenancy.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>What a permission change writes (ADR-0035 step 3): search by scope, subtree moves, live event audiences.</summary>
public sealed class ScopeFanOutTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(HttpClient Admin, TenantSummary Tenant, Guid Workspace, Guid List, Dictionary<string, Guid> Users)
    {
        public string ListUrl => $"/v1.0/workspaces/{Workspace}/lists/{List}";
    }

    private async Task<Setup> SetupAsync(string tenant, params string[] members)
    {
        var summary = await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var ws = await admin.CreateWorkspaceAsync("Team");
        var list = await admin.CreateListAsync(ws, "Docs", await admin.CreateContentTypeAsync("Doc", []));
        var users = new Dictionary<string, Guid>();
        foreach (var name in members)
        {
            var created = await admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            users[name] = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
            await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/members", new { userId = users[name], role = "member" }, Ct);
        }

        return new Setup(admin, summary, ws, list, users);
    }

    private Task<HttpClient> AsAsync(Setup setup, string user) => ApiClient.CreateAsync(factory, setup.Tenant.Identifier, user, $"{user}-password-1");

    private static async Task<Guid> CreateAsync(HttpClient client, Setup setup, string title, Guid? parentId = null, bool isFolder = false) =>
        (await client.CreateItemAsync(setup.Workspace, setup.List, new { parentId, isFolder, fields = new { title } })).GetProperty("id").GetGuid();

    private static async Task<List<string>> SearchAsync(HttpClient client, string q) =>
        (await (await client.GetAsync($"/v1.0/search?q={q}", Ct)).ReadJsonAsync()).GetProperty("value").EnumerateArray()
            .Select(h => h.GetProperty("title").GetString()!).Order(StringComparer.Ordinal).ToList();

    private AsyncServiceScope Scope(Setup setup, Guid? user = null) =>
        factory.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(setup.Tenant.Id, setup.Tenant.Identifier, user);

    [Fact]
    public async Task Grant_changes_reach_search_at_once_without_reindexing()
    {
        var setup = await SetupAsync("fan-search", "alice");
        var folder = await CreateAsync(setup.Admin, setup, "Audit", isFolder: true);
        await setup.Admin.PostAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        var report = await CreateAsync(setup.Admin, setup, "Quarterly audit report", folder);
        await Eventually.WaitForAsync<bool>(async () => (await SearchAsync(setup.Admin, "quarterly")).Count == 1 ? true : null);
        List<Guid> passages;
        await using (var scope = Scope(setup))
        {
            passages = await scope.ServiceProvider.GetRequiredService<SearchDbContext>().Passages.Where(p => p.DocumentId == report).Select(p => p.Id).ToListAsync(Ct);
        }

        var alice = await AsAsync(setup, "alice");
        Assert.Empty(await SearchAsync(alice, "quarterly"));

        // The grant writes the folder's access list only: search trims by scope, so alice finds it right away.
        var grants = new[] { new { principalType = "user", principalId = setup.Users["alice"], level = "read" } };
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.PutAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/grants", new { grants }, Ct)).StatusCode);
        Assert.Equal(["Quarterly audit report"], await SearchAsync(alice, "quarterly"));
        Assert.Equal(HttpStatusCode.OK, (await setup.Admin.PutAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/grants", new { grants = Array.Empty<object>() }, Ct)).StatusCode);
        Assert.Empty(await SearchAsync(alice, "quarterly"));

        // Resetting the folder moves the document back to the list's scope: the scope column changes, the passages stay.
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Admin.PostAsync($"{setup.ListUrl}/items/{folder}/permissions/resetInheritance", null, Ct)).StatusCode);
        await Eventually.WaitForAsync<bool>(async () => (await SearchAsync(alice, "quarterly")).Count == 1 ? true : null);
        await using (var scope = Scope(setup))
        {
            var after = await scope.ServiceProvider.GetRequiredService<SearchDbContext>().Passages.Where(p => p.DocumentId == report).Select(p => p.Id).ToListAsync(Ct);
            Assert.Equal(passages.Order(), after.Order());

            // The folder's access list is gone with its scope.
            Assert.False(await scope.ServiceProvider.GetRequiredService<ListsDbContext>().AclEntries.AnyAsync(e => e.ScopeId == folder, Ct));
        }
    }

    [Fact]
    public async Task Folders_the_request_does_not_finish_are_moved_in_the_background()
    {
        var setup = await SetupAsync("fan-move", "alice");
        var folder = await CreateAsync(setup.Admin, setup, "Contracts", isFolder: true);
        var sub = await CreateAsync(setup.Admin, setup, "2026", folder, isFolder: true);
        var documents = new List<Guid>
        {
            await CreateAsync(setup.Admin, setup, "Lease", folder),
            await CreateAsync(setup.Admin, setup, "Loan", sub),
            await CreateAsync(setup.Admin, setup, "Lien", sub),
        };

        // Break inheritance on the folder alone, as a request that moved nothing inside it (over its inline limit).
        await using (var scope = Scope(setup))
        {
            var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
            var item = await db.Items.SingleAsync(i => i.Id == folder, Ct);
            var list = await db.Lists.SingleAsync(l => l.Id == setup.List, Ct);
            db.AclEntries.Add(Acl.Owners(list, folder));
            item.HasUniquePermissions = true;
            item.ScopeId = folder;
            await db.SaveChangesAsync(Ct);

            var mover = ActivatorUtilities.CreateInstance<ScopeMover>(
                scope.ServiceProvider, Microsoft.Extensions.Options.Options.Create(new ListsOptions { ScopeMoveInlineLimit = 0 }));
            Assert.False(await mover.MoveAsync(setup.List, folder, setup.List, folder, inline: true, Ct));
            Assert.True(await db.Items.Where(i => documents.Contains(i.Id)).AllAsync(i => i.ScopeId == setup.List, Ct));
        }

        // Until the background move, the documents keep their old access (the window ADR-0035 allows).
        var alice = await AsAsync(setup, "alice");
        Assert.Equal(["2026", "Lease", "Lien", "Loan"], (await alice.QueryTitlesAsync(setup.Workspace, setup.List, "")).Order(StringComparer.Ordinal));

        var message = new CompleteFolderScopeChange(setup.List, folder, setup.List, folder, setup.Tenant.Id, setup.Tenant.Identifier, null);
        await CompleteFolderScopeChangeHandler.Handle(message, factory.Services.GetRequiredService<ITenantScopeFactory>(), Ct);
        Assert.Empty(await alice.QueryTitlesAsync(setup.Workspace, setup.List, ""));
        await using (var scope = Scope(setup))
        {
            var db = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
            Assert.True(await db.Items.Where(i => documents.Contains(i.Id) || i.Id == sub).AllAsync(i => i.ScopeId == folder, Ct));

            // Every moved item (and the folder itself) is logged for delta with the scope it came from.
            var logged = await db.ItemChanges.Where(c => c.FromScopeId == setup.List && c.ScopeId == folder).Select(c => c.ItemId!.Value).ToListAsync(Ct);
            Assert.Equal(documents.Append(sub).Append(folder).Order(), logged.Order());
        }
    }

    [Fact]
    public async Task Item_events_reach_only_people_who_can_read_the_item()
    {
        var setup = await SetupAsync("fan-live", "alice", "bob");
        var folder = await CreateAsync(setup.Admin, setup, "Alice only", isFolder: true);
        await setup.Admin.PostAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        await setup.Admin.PutAsJsonAsync($"{setup.ListUrl}/items/{folder}/permissions/grants",
            new { grants = new[] { new { principalType = "user", principalId = setup.Users["alice"], level = "read" } } }, Ct);

        await using var alice = await EventsAsync(await AsAsync(setup, "alice"));
        await using var bob = await EventsAsync(await AsAsync(setup, "bob"));
        var secret = await CreateAsync(setup.Admin, setup, "Secret", folder);
        var open = await CreateAsync(setup.Admin, setup, "Open");

        // Events arrive in order: bob's first item event is the open one, alice gets both.
        Assert.Equal(open, await bob.NextItemAsync());
        Assert.Equal(secret, await alice.NextItemAsync());
        Assert.Equal(open, await alice.NextItemAsync());
    }

    private static async Task<EventStream> EventsAsync(HttpClient client)
    {
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/v1.0/me/events"), HttpCompletionOption.ResponseHeadersRead, Ct);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        Assert.Equal("event: connected", await reader.ReadLineAsync(Ct));
        return new EventStream(response, reader);
    }

    private sealed class EventStream(HttpResponseMessage response, StreamReader reader) : IAsyncDisposable
    {
        public async Task<Guid> NextItemAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            string? type = null;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    type = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal) && type == "item.changed")
                {
                    return JsonDocument.Parse(line[6..]).RootElement.GetProperty("itemId").GetGuid();
                }
            }

            throw new InvalidOperationException("The event stream ended.");
        }

        public ValueTask DisposeAsync()
        {
            reader.Dispose();
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
