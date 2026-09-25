using System.Net.Http.Json;
using System.Text.Json;
using PaperDotNet.Cli;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PaperDotNet.IntegrationTests;

/// <summary>The client CLI <c>pdn</c> (API-13) against the in-memory server.</summary>
public sealed class ClientCliTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddText(text, 24, new PdfPoint(40, 760), builder.AddStandard14Font(Standard14Font.Helvetica));
        return builder.Build();
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(string tenant, string token, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ClientCli.RunAsync(
            [.. args, "--url", "http://localhost/", "--token", token, "--tenant", tenant], _ => factory.CreateClient(), output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Pdn_uploads_folders_downloads_and_searches()
    {
        await factory.CreateTenantAsync("cli");
        var admin = await ApiClient.CreateAsync(factory, "cli");
        var token = await ApiClient.GetTokenAsync(await ApiClient.CreateAsync(factory, "cli", userName: null), "admin", PaperDotNetApiFactory.AdminPassword);
        var ws = await admin.CreateWorkspaceAsync("Office");
        var library = (await (await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Archive", templateKey = "documents" }, Ct)).ReadJsonAsync())
            .GetProperty("id").GetGuid();

        var (exit, output, _) = await RunAsync("cli", token, "workspaces");
        Assert.Equal(0, exit);
        Assert.Contains($"{ws}  Office", output, StringComparison.Ordinal);
        Assert.Contains($"{library}  Archive", (await RunAsync("cli", token, "libraries", "-w", "Office")).Output, StringComparison.Ordinal);

        // A folder tree: its structure becomes folders under --folder; other files are skipped.
        var root = Directory.CreateTempSubdirectory("pdn_cli_");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "Bank"));
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "contract.pdf"), Pdf("Rental contract"), Ct);
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "Bank", "statement.pdf"), Pdf("Bank statement zebra"), Ct);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "notes.txt"), "not a document", Ct);

            (exit, output, var error) = await RunAsync("cli", token, "upload", root.FullName, "-w", "Office", "-l", "Archive", "-f", "2026");
            Assert.True(exit == 0, error);
            Assert.Contains("notes.txt: skipped", output, StringComparison.Ordinal);

            var items = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}/items?$top=100", Ct)).ReadJsonAsync())
                .GetProperty("value").EnumerateArray().ToList();
            string Title(JsonElement item) => item.GetProperty("fields").GetProperty("title").GetString()!;
            Guid? Parent(JsonElement item) => item.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null;
            var year = items.Single(i => Title(i) == "2026");
            var bank = items.Single(i => Title(i) == "Bank");
            Assert.Equal(year.GetProperty("id").GetGuid(), Parent(bank));
            var statement = items.Single(i => Title(i) == "statement");
            Assert.Equal(bank.GetProperty("id").GetGuid(), Parent(statement));
            Assert.Equal(year.GetProperty("id").GetGuid(), Parent(items.Single(i => Title(i) == "contract")));

            // Uploading again reuses the folders.
            Assert.Equal(0, (await RunAsync("cli", token, "upload", Path.Combine(root.FullName, "Bank"), "-w", "Office", "-l", "Archive", "-f", "2026/Bank")).Exit);
            var folders = (await (await admin.GetAsync($"/v1.0/workspaces/{ws}/lists/{library}/items?$filter={Uri.EscapeDataString("isFolder eq true")}", Ct)).ReadJsonAsync())
                .GetProperty("value").GetArrayLength();
            Assert.Equal(2, folders);

            // Download.
            var target = Path.Combine(root.FullName, "download.pdf");
            (exit, _, error) = await RunAsync("cli", token, "download", "-w", ws.ToString(), "-l", "Archive", "-i", statement.GetProperty("id").GetGuid().ToString(), "-o", target);
            Assert.True(exit == 0, error);
            Assert.Equal(Pdf("Bank statement zebra").Length, new FileInfo(target).Length);

            // Search finds the text once processed.
            await Eventually.WaitForAsync<bool>(async () =>
                (await RunAsync("cli", token, "search", "zebra")).Output.Contains("statement", StringComparison.Ordinal) ? true : null, TimeSpan.FromSeconds(60));

            // Errors: unknown library, both targets.
            (exit, _, error) = await RunAsync("cli", token, "upload", root.FullName, "-w", "Office", "-l", "Nope");
            Assert.Equal(1, exit);
            Assert.Contains("No library 'Nope'", error, StringComparison.Ordinal);
            Assert.Equal(1, (await RunAsync("cli", token, "upload", root.FullName, "--inbox", "-w", "Office", "-l", "Archive")).Exit);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
