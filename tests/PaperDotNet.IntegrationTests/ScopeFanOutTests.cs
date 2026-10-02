using System.Net;
using System.Text.Json;

namespace PaperDotNet.IntegrationTests;

/// <summary>What a permission change reaches at once (ADR-0035): search trimmed by scope and live event audiences.</summary>
public sealed class ScopeFanOutTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new();
    private HttpClient _admin = null!;
    private string _workspace = "";
    private string _list = "";
    private readonly Dictionary<string, string> _users = [];

    private string ListUrl => $"/v1.0/workspaces/{_workspace}/lists/{_list}";

    public async ValueTask InitializeAsync()
    {
        _admin = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_admin, "Team");
        _list = (await Api.CreateListAsync(_admin, _workspace, "Docs")).Id();
        foreach (var name in new[] { "alice", "bob" })
        {
            using var created = await _admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            _users[name] = (await created.JsonAsync(HttpStatusCode.Created)).Id();
            await _admin.PostAsJsonAsync($"/v1.0/workspaces/{_workspace}/members", new { userId = _users[name], role = "member" }, Ct);
        }
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private Task<HttpClient> AsAsync(string user) => _host.SignInAsync(user, $"{user}-password-1");

    private async Task<string> CreateAsync(string title, string? parentId = null, bool isFolder = false)
    {
        using var response = await _admin.PostAsJsonAsync($"{ListUrl}/items", new { parentId, isFolder, fields = new { title } }, Ct);
        return (await response.JsonAsync(HttpStatusCode.Created)).Id();
    }

    private static async Task<List<string>> SearchAsync(HttpClient client, string q) =>
        [.. (await (await client.GetAsync($"/v1.0/search?q={q}", Ct)).JsonAsync(HttpStatusCode.OK)).GetProperty("value").EnumerateArray()
            .Select(h => h.GetProperty("title").GetString()!).Order(StringComparer.Ordinal)];

    private static async Task<List<string>> EventuallyAsync(Func<Task<List<string>>> read, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var value = await read();
        while (value.Count != count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
            value = await read();
        }

        return value;
    }

    [Fact]
    public async Task Grant_changes_reach_search_at_once_without_reindexing()
    {
        var folder = await CreateAsync("Audit", isFolder: true);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"{ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct)).StatusCode);
        await CreateAsync("Quarterly audit report", folder);
        Assert.Single(await EventuallyAsync(() => SearchAsync(_admin, "quarterly"), 1));
        var alice = await AsAsync("alice");
        Assert.Empty(await SearchAsync(alice, "quarterly"));

        // The grant writes the folder's access list only: search trims by scope, so alice finds it right away.
        var grants = new[] { new { principalType = "user", principalId = _users["alice"], level = "read" } };
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"{ListUrl}/items/{folder}/permissions/grants", new { grants }, Ct)).StatusCode);
        Assert.Equal(["Quarterly audit report"], await SearchAsync(alice, "quarterly"));
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"{ListUrl}/items/{folder}/permissions/grants", new { grants = Array.Empty<object>() }, Ct)).StatusCode);
        Assert.Empty(await SearchAsync(alice, "quarterly"));

        // Resetting the folder moves the document back to the list's scope.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"{ListUrl}/items/{folder}/permissions/resetInheritance", null, Ct)).StatusCode);
        Assert.Single(await EventuallyAsync(() => SearchAsync(alice, "quarterly"), 1));
    }

    [Fact]
    public async Task Item_events_reach_only_people_who_can_read_the_item()
    {
        var folder = await CreateAsync("Alice only", isFolder: true);
        await _admin.PostAsJsonAsync($"{ListUrl}/items/{folder}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        await _admin.PutAsJsonAsync($"{ListUrl}/items/{folder}/permissions/grants",
            new { grants = new[] { new { principalType = "user", principalId = _users["alice"], level = "read" } } }, Ct);

        await using var alice = await EventsAsync(await AsAsync("alice"));
        await using var bob = await EventsAsync(await AsAsync("bob"));
        var secret = await CreateAsync("Secret", folder);
        var open = await CreateAsync("Open");

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
        public async Task<string> NextItemAsync()
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
                    return JsonElement.Parse(line[6..]).GetProperty("itemId").GetString()!;
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
