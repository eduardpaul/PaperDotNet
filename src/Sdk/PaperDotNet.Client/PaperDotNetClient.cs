using System.Net.Http.Headers;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace PaperDotNet.Client;

/// <summary>
/// Creates the generated <see cref="PaperDotNetApiClient"/> for a PaperDotNet installation. Authenticate with
/// an API token or an OAuth access token; name the tenant when the installation does not resolve it from the host.
/// </summary>
/// <example>
/// <code>
/// var api = PaperDotNetClient.Create(new Uri("https://dms.example.com"), apiToken);
/// var workspaces = await api.V10.Workspaces.GetAsync();
/// </code>
/// </example>
public static class PaperDotNetClient
{
    /// <summary>A client with its own <see cref="HttpClient"/>.</summary>
    public static PaperDotNetApiClient Create(Uri baseAddress, string accessToken, string? tenant = null) =>
        Create(new HttpClient { BaseAddress = baseAddress }, accessToken, tenant);

    /// <summary>
    /// A client on an existing <see cref="HttpClient"/> (e.g. from <c>IHttpClientFactory</c>); its
    /// <see cref="HttpClient.BaseAddress"/> is the installation's address.
    /// </summary>
    public static PaperDotNetApiClient Create(HttpClient httpClient, string? accessToken = null, string? tenant = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (accessToken is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (tenant is not null)
        {
            httpClient.DefaultRequestHeaders.Remove("X-Tenant");
            httpClient.DefaultRequestHeaders.Add("X-Tenant", tenant);
        }

        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: httpClient)
        {
            BaseUrl = httpClient.BaseAddress?.ToString().TrimEnd('/'),
        };
        return new PaperDotNetApiClient(adapter);
    }
}
