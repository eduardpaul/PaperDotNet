namespace PaperDotNet.Host.Cli;

/// <summary>
/// <c>paperdotnet healthcheck</c>: probes the local liveness endpoint. Used by
/// the container HEALTHCHECK so the image needs no curl.
/// </summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var url = args.Length > 1 ? args[1] : $"http://127.0.0.1:{Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080"}/health/live";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(new Uri(url));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
