using Microsoft.Kiota.Abstractions;
using PaperDotNet.Client;
using PaperDotNet.Client.Models;

namespace PaperDotNet.IntegrationTests;

/// <summary>The generated C# SDK (API-03) against a running API.</summary>
public sealed class ClientSdkTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_generated_client_creates_and_reads_resources()
    {
        await factory.CreateTenantAsync("sdk-csharp");
        var api = PaperDotNetClient.Create(await ApiClient.CreateAsync(factory, "sdk-csharp"));

        var created = await api.V10.Workspaces.PostAsync(new CreateWorkspaceRequest { Name = "From the SDK", Description = "Typed" }, cancellationToken: Ct);
        Assert.NotNull(created?.Id);

        var page = await api.V10.Workspaces.GetAsync(cancellationToken: Ct);
        Assert.Contains(page!.Value!, w => w.Id == created!.Id && w.Name == "From the SDK");
        var one = await api.V10.Workspaces[created!.Id!.Value].GetAsync(cancellationToken: Ct);
        Assert.Equal("Typed", one!.Description);

        var me = await api.V10.Me.GetAsync(cancellationToken: Ct);
        Assert.Equal("admin", me!.UserName);

        // Problems surface as exceptions with the status code.
        var missing = await Assert.ThrowsAnyAsync<ApiException>(() => api.V10.Workspaces[Guid.NewGuid()].GetAsync(cancellationToken: Ct));
        Assert.Equal(404, missing.ResponseStatusCode);
    }
}
