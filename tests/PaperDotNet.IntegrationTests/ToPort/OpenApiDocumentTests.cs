using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperDotNet.IntegrationTests;

/// <summary>
/// The SDKs (API-03) are generated from <c>sdk/openapi.json</c>. This test keeps it in sync with the API:
/// run it with <c>PAPERDOTNET_UPDATE_OPENAPI=1</c> to rewrite the file, then run <c>sdk/generate.sh</c>.
/// </summary>
public sealed class OpenApiDocumentTests(PaperDotNetApiFactory factory)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string DocumentPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PaperDotNet.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "sdk", "openapi.json");
        }
    }

    [Fact]
    public async Task The_committed_openapi_document_is_current()
    {
        Assert.SkipWhen(PaperDotNetApiFactory.Provider != "sqlite", "The document does not depend on the database.");
        var client = factory.CreateClient();
        var current = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!;
        current.AsObject().Remove("servers");
        var text = current.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable("PAPERDOTNET_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(DocumentPath, text, TestContext.Current.CancellationToken);
        }

        var committed = File.Exists(DocumentPath) ? (await File.ReadAllTextAsync(DocumentPath, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n") : "";
        Assert.True(committed == text, "sdk/openapi.json is outdated: run the tests with PAPERDOTNET_UPDATE_OPENAPI=1, then sdk/generate.sh.");
    }
}
