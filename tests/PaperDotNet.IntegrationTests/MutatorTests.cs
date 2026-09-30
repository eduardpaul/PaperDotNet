using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Lists.Contracts;

namespace PaperDotNet.IntegrationTests;

/// <summary>Item mutators (ADR-0023) change or cancel item writes before they are saved.</summary>
public sealed class MutatorTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new(services => services.AddScoped<IItemMutator, TestMutator>());
    private HttpClient _client = null!;
    private string _workspace = "";

    public async ValueTask InitializeAsync()
    {
        _client = await _host.SignInAsync();
        _workspace = await Api.CreateWorkspaceAsync(_client, "Events");
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private Task<string> ListAsync(string name) => Api.CreateListAsync(_client, _workspace, name, new object[]
    {
        new { name = "amount", type = "number" },
        new { name = "status", type = "choice", choices = new[] { "open", "paid" }, defaultValue = "open" },
        new { name = "code", type = "text" },
    }).ContinueWith(t => t.Result.Id(), TaskScheduler.Default);

    [Fact]
    public async Task Mutators_can_modify_and_cancel_writes()
    {
        var list = await ListAsync("Hooked");
        var modified = await Api.CreateItemAsync(_client, _workspace, list, new { title = "A", code = "abc" });
        using var cancelled = await _client.PostAsJsonAsync(Api.Items(_workspace, list), new { fields = new { title = "forbidden" } }, Ct);
        using var invalid = await _client.PostAsJsonAsync(Api.Items(_workspace, list), new { fields = new { title = "invalid-by-mutator" } }, Ct);

        Assert.Equal("ABC", modified.GetProperty("fields").GetProperty("code").GetString());
        var problem = await cancelled.JsonAsync(HttpStatusCode.Conflict);
        Assert.Equal("cancelledByMutator", problem.GetProperty("code").GetString());
        Assert.Contains("forbidden", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Other lists are not affected.
        var plain = await ListAsync("Plain");
        Assert.Equal("abc", (await Api.CreateItemAsync(_client, _workspace, plain, new { title = "B", code = "abc" })).GetProperty("fields").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Updating_and_deleting_mutators_can_cancel()
    {
        var list = await ListAsync("Hooked");
        var paid = await Api.CreateItemAsync(_client, _workspace, list, new { title = "Invoice", status = "paid" });
        var keep = await Api.CreateItemAsync(_client, _workspace, list, new { title = "keep me" });
        string Url(System.Text.Json.JsonElement item) => $"{Api.Items(_workspace, list)}/{item.Id()}";

        using var update = await _client.SendAsync(Api.Patch(Url(paid), new { fields = new { amount = 5 } }, paid.ETag()), Ct);
        using var delete = await _client.SendAsync(Api.WithETag(HttpMethod.Delete, Url(keep), null, keep.ETag()), Ct);

        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Url(keep), Ct)).StatusCode);
    }

    /// <summary>Acts on lists named "Hooked" only.</summary>
    private sealed class TestMutator : IItemMutator
    {
        public int Sequence => 10;

        public bool AppliesTo(ItemEventScope scope) => scope.ListName == "Hooked";

        public ValueTask ItemAddingAsync(ItemMutationContext context, CancellationToken cancellationToken)
        {
            var title = context.After!["title"]!.GetValue<string>();
            if (title == "forbidden")
            {
                context.Cancel("Titles cannot be 'forbidden'.");
            }
            else if (title == "invalid-by-mutator")
            {
                context.After["amount"] = "not a number";
            }
            else if (context.After["code"] is { } code)
            {
                context.After["code"] = code.GetValue<string>().ToUpperInvariant();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ItemUpdatingAsync(ItemMutationContext context, CancellationToken cancellationToken)
        {
            if (context.Before!["status"]?.GetValue<string>() == "paid")
            {
                context.Cancel("Paid items are locked.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ItemDeletingAsync(ItemMutationContext context, CancellationToken cancellationToken)
        {
            if (context.Before!["title"]!.GetValue<string>().StartsWith("keep", StringComparison.Ordinal))
            {
                context.Cancel("This item must be kept.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
