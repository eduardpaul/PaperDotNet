using System.Diagnostics.CodeAnalysis;
using JasperFx;
using PaperDotNet.Host;
using PaperDotNet.Identity.Features;
using PaperDotNet.Persistence.Sqlite;

// One binary: `paperdotnet` serves the API; `paperdotnet healthcheck` probes it (container HEALTHCHECK);
// `paperdotnet codegen write` (JIT build only) writes the Wolverine handler code that the AOT build compiles in.
if (args is ["healthcheck", ..])
{
    return await HealthProbe.RunAsync(args);
}

var generatingCode = args is ["codegen", ..];
var builder = WebApplication.CreateSlimBuilder(generatingCode ? [] : args);

// Settings from PAPERDOTNET__Section__Key environment variables (deploy/.env), over appsettings.json.
builder.Configuration.AddEnvironmentVariables("PAPERDOTNET__");
builder.AddPaperDotNet(generatingCode);
var app = builder.Build();

if (generatingCode)
{
    return await CodeGeneration.RunAsync(app, args);
}

await SqliteDatabase.MigrateAsync(app.Services);
await Bootstrap.RunAsync(app.Services);
app.UsePaperDotNet();
await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;

internal static class CodeGeneration
{
    /// <summary>
    /// Development only (eng/codegen.sh, on the JIT build). Not guarded by <c>RuntimeFeature.IsDynamicCodeSupported</c>:
    /// PublishAot turns that off in JIT builds too, and writing code needs no dynamic code.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Development command, run on the JIT build (eng/codegen.sh).")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Development command, run on the JIT build (eng/codegen.sh).")]
    public static Task<int> RunAsync(WebApplication app, string[] args) => app.RunJasperFxCommands(args);
}

internal static class HealthProbe
{
    /// <summary>Exit code 0 when <c>/health</c> answers 200 (default <c>http://localhost:8080</c>).</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var baseUrl = args.Length > 1 ? args[1] : $"http://localhost:{Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080"}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(new Uri(new Uri(baseUrl), "/health"));
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
