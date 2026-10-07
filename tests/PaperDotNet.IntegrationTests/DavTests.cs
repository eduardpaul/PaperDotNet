using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace PaperDotNet.IntegrationTests;

/// <summary>WebDAV for libraries (API-10, ADR-0047): the read-only mount at /dav.</summary>
public sealed class DavTests(PaperDotNetApiFactory factory)
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace Ms = "urn:schemas-microsoft-com:";
    private static readonly string[] ReadScopes = ["list.read", "document.read"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Pdf(string marker) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% {marker}\n%%EOF\n");

    private sealed record Library(HttpClient Admin, HttpClient Dav, string Tenant, Guid Workspace, Guid List)
    {
        public string Url => $"/v1.0/workspaces/{Workspace}/lists/{List}";
    }

    /// <summary>Workspace "Projects" with the library "Contracts", and a WebDAV client for the administrator.</summary>
    private async Task<Library> SetupAsync(string tenant)
    {
        await factory.CreateTenantAsync(tenant);
        var admin = await ApiClient.CreateAsync(factory, tenant);
        var ws = await admin.CreateWorkspaceAsync("Projects");
        var created = await admin.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists", new { name = "Contracts", templateKey = "documents" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var list = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        return new Library(admin, await DavClientAsync(admin, tenant, ReadScopes), tenant, ws, list);
    }

    /// <summary>A client that sends HTTP Basic with a new API token of <paramref name="api"/>'s user as the password.</summary>
    private async Task<HttpClient> DavClientAsync(HttpClient api, string tenant, string[] scopes)
    {
        var created = await api.PostAsJsonAsync("/v1.0/me/apiTokens", new { name = "webdav", scopes }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var secret = (await created.ReadJsonAsync()).GetProperty("secret").GetString();
        return Basic(tenant, $"anyone:{secret}");
    }

    private HttpClient Basic(string tenant, string credentials)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        return client;
    }

    private static async Task<Guid> UploadAsync(Library library, byte[] content, string fileName, string title, Guid? folderId = null)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName }, { new StringContent(title), "title" } };
        if (folderId is { } folder)
        {
            form.Add(new StringContent(folder.ToString()), "folderId");
        }

        var response = await library.Admin.PostAsync($"{library.Url}/documents", form, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.ReadJsonAsync()).GetProperty("itemId").GetGuid();
    }

    private static async Task<Guid> FolderAsync(Library library, string title) =>
        (await library.Admin.CreateItemAsync(library.Workspace, library.List, new { isFolder = true, fields = new { title } })).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, string? depth = "1", Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (depth is not null)
        {
            request.Headers.Add("Depth", depth);
        }

        configure?.Invoke(request);
        return client.SendAsync(request, Ct);
    }

    /// <summary>PROPFIND: href → properties.</summary>
    private static async Task<Dictionary<string, XElement>> PropFindAsync(HttpClient client, string url, string depth = "1")
    {
        var response = await SendAsync(client, "PROPFIND", url, depth);
        Assert.True(response.StatusCode == (HttpStatusCode)207, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.Root!.Elements(D + "response").ToDictionary(
            r => r.Element(D + "href")!.Value,
            r => new XElement("props", r.Elements(D + "propstat").Where(p => p.Element(D + "status")!.Value.Contains(" 200 ", StringComparison.Ordinal))
                .SelectMany(p => p.Element(D + "prop")!.Elements())));
    }

    /// <summary>The unescaped names directly below <paramref name="url"/>.</summary>
    private static async Task<List<string>> NamesAsync(HttpClient client, string url) =>
        (await PropFindAsync(client, url)).Keys
            .Where(href => href.TrimEnd('/') != url.TrimEnd('/'))
            .Select(href => Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public async Task Options_is_anonymous_and_advertises_a_read_only_server()
    {
        await factory.CreateTenantAsync("dav-options");
        var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Tenant", "dav-options");

        var response = await SendAsync(anonymous, "OPTIONS", "/dav/", depth: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1", Assert.Single(response.Headers.GetValues("DAV")));
        Assert.Equal("DAV", Assert.Single(response.Headers.GetValues("MS-Author-Via")));
        var root = await SendAsync(anonymous, "OPTIONS", "/", depth: null);
        Assert.Equal("1", Assert.Single(root.Headers.GetValues("DAV")));
    }

    [Fact]
    public async Task Requests_without_a_valid_token_get_a_basic_challenge_and_basic_works_only_under_dav()
    {
        var library = await SetupAsync("dav-auth");
        var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Tenant", library.Tenant);

        var challenge = await SendAsync(anonymous, "PROPFIND", "/dav/");
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Equal("Basic", challenge.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(Basic(library.Tenant, "admin:pdn_not-a-real-token-at-all"), "PROPFIND", "/dav/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(Basic(library.Tenant, $"admin:{PaperDotNetApiFactory.AdminPassword}"), "PROPFIND", "/dav/")).StatusCode);

        // The same Basic credentials that work under /dav are never read by the API (they are ambient, like cookies).
        Assert.Equal((HttpStatusCode)207, (await SendAsync(library.Dav, "PROPFIND", "/dav/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await library.Dav.GetAsync("/v1.0/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_token_needs_the_read_scopes()
    {
        var library = await SetupAsync("dav-scopes");
        var listsOnly = await DavClientAsync(library.Admin, library.Tenant, ["list.read"]);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(listsOnly, "PROPFIND", "/dav/")).StatusCode);
    }

    [Fact]
    public async Task The_tree_has_home_shared_workspaces_and_only_document_libraries()
    {
        var library = await SetupAsync("dav-tree");
        var type = await library.Admin.CreateContentTypeAsync("Task", []);
        await library.Admin.CreateListAsync(library.Workspace, "Tasks", type);

        // Another user's Home is a workspace the administrator manages, but it is not theirs to browse.
        await library.Admin.PostAsJsonAsync("/v1.0/users", new { userName = "carol", password = "carol-password-1" }, Ct);
        var carol = await ApiClient.CreateAsync(factory, library.Tenant, "carol", "carol-password-1");
        await (await DavClientAsync(carol, library.Tenant, ReadScopes)).SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/"), Ct);

        Assert.Equal(["Home", "Projects"], await NamesAsync(library.Dav, "/dav/"));
        Assert.Equal(["Contracts"], await NamesAsync(library.Dav, "/dav/Projects/"));
        Assert.Equal(["Documents", "Inbox"], await NamesAsync(library.Dav, "/dav/Home/"));
        var props = (await PropFindAsync(library.Dav, "/dav/Projects/Contracts/", "0")).Single().Value;
        Assert.NotNull(props.Element(D + "resourcetype")!.Element(D + "collection"));
    }

    [Fact]
    public async Task Names_are_windows_safe_unique_and_stable()
    {
        var library = await SetupAsync("dav-names");
        var first = await UploadAsync(library, Pdf("a"), "a.pdf", "Invoice");
        var second = await UploadAsync(library, Pdf("b"), "b.pdf", "Invoice");
        await UploadAsync(library, Pdf("c"), "c.pdf", "invoice");
        await UploadAsync(library, Pdf("d"), "d.pdf", "Report: Q1/2025?");
        await UploadAsync(library, Pdf("e"), "e.pdf", "CON");
        var folder = await FolderAsync(library, "Ä Umlaut & #hash 100%");
        await UploadAsync(library, Pdf("inner"), "inner.pdf", "Inner", folder);

        Assert.Equal(
            ["CON_.pdf", "Invoice (2).pdf", "Invoice.pdf", "Report_ Q1_2025_.pdf", "invoice (3).pdf", "Ä Umlaut & #hash 100%"],
            await NamesAsync(library.Dav, "/dav/Projects/Contracts/"));

        // The oldest keeps the name; hrefs are escaped and lead back to the same entries.
        Assert.Contains("% a", await library.Dav.GetStringAsync("/dav/Projects/Contracts/Invoice.pdf", Ct));
        Assert.Contains("% b", await library.Dav.GetStringAsync("/dav/Projects/Contracts/Invoice%20(2).pdf", Ct));
        var href = (await PropFindAsync(library.Dav, "/dav/Projects/Contracts/")).Keys.Single(h => h.Contains("Umlaut", StringComparison.Ordinal));
        Assert.Contains("%23hash", href, StringComparison.Ordinal);
        Assert.Contains("100%25", href, StringComparison.Ordinal);
        Assert.Equal(["Inner.pdf"], await NamesAsync(library.Dav, href));
        Assert.Contains("% inner", await library.Dav.GetStringAsync(href + "Inner.pdf", Ct));

        // Windows paths are case-insensitive.
        Assert.Contains("% a", await library.Dav.GetStringAsync("/dav/projects/CONTRACTS/invoice.PDF", Ct));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Files_download_with_ranges_etags_and_windows_properties()
    {
        var library = await SetupAsync("dav-files");
        var content = new byte[64 * 1024];
        Random.Shared.NextBytes(content);
        "%PDF-1.4\n"u8.CopyTo(content);
        await UploadAsync(library, content, "big.pdf", "Big");
        const string url = "/dav/Projects/Contracts/Big.pdf";

        var get = await library.Dav.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(content, await get.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(content.Length, get.Content.Headers.ContentLength);
        Assert.Equal("application/pdf", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", get.Content.Headers.ContentDisposition!.DispositionType);
        var etag = get.Headers.ETag!;
        Assert.False(etag.IsWeak);

        var head = await library.Dav.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), Ct);
        Assert.Equal(content.Length, head.Content.Headers.ContentLength);

        var range = new HttpRequestMessage(HttpMethod.Get, url);
        range.Headers.Range = new RangeHeaderValue(10, 19);
        var partial = await library.Dav.SendAsync(range, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(content[10..20], await partial.Content.ReadAsByteArrayAsync(Ct));

        var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await library.Dav.SendAsync(conditional, Ct)).StatusCode);

        var props = (await PropFindAsync(library.Dav, url, "0")).Single().Value;
        Assert.Equal(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), props.Element(D + "getcontentlength")!.Value);
        Assert.Equal(etag.Tag, props.Element(D + "getetag")!.Value);
        Assert.Equal("application/pdf", props.Element(D + "getcontenttype")!.Value);
        Assert.Equal("00000001", props.Element(Ms + "Win32FileAttributes")!.Value);
    }

    [Fact]
    public async Task Files_of_any_type_are_listed_and_never_run_in_the_browser()
    {
        var library = await SetupAsync("dav-any-type");
        await UploadAsync(library, [0x50, 0x4B, 0x03, 0x04, 1, 2], "Report.docx", "Quarterly report");
        await UploadAsync(library, Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "page.html", "Page");
        await UploadAsync(library, [], "empty.txt", "Empty");

        Assert.Equal(["Empty.txt", "Page.html", "Quarterly report.docx"], await NamesAsync(library.Dav, "/dav/Projects/Contracts/"));

        var html = await library.Dav.GetAsync("/dav/Projects/Contracts/Page.html", Ct);
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Equal("text/html", html.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(html.Headers.GetValues("X-Content-Type-Options")));
        Assert.StartsWith("sandbox", Assert.Single(html.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);

        var empty = await library.Dav.GetAsync("/dav/Projects/Contracts/Empty.txt", Ct);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty(await empty.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Infinite_depth_and_writes_are_refused()
    {
        var library = await SetupAsync("dav-readonly");
        await UploadAsync(library, Pdf("x"), "x.pdf", "Doc");

        var infinite = await SendAsync(library.Dav, "PROPFIND", "/dav/Projects/", "infinity");
        Assert.Equal(HttpStatusCode.Forbidden, infinite.StatusCode);
        Assert.Contains("propfind-finite-depth", await infinite.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var put = await library.Dav.PutAsync("/dav/Projects/Contracts/New.pdf", new ByteArrayContent(Pdf("new")), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await library.Dav.PutAsync("/dav/Projects/Contracts/Doc.pdf", new ByteArrayContent(Pdf("v2")), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(library.Dav, "MKCOL", "/dav/Projects/Contracts/Folder", depth: null)).StatusCode);
        foreach (var method in new[] { "MOVE", "COPY" })
        {
            var response = await SendAsync(library.Dav, method, "/dav/Projects/Contracts/Doc.pdf", depth: null,
                r => r.Headers.Add("Destination", "/dav/Projects/Contracts/Other.pdf"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(library.Dav, "LOCK", "/dav/Projects/Contracts/Doc.pdf", depth: null)).StatusCode);
        Assert.Equal(["Doc.pdf"], await NamesAsync(library.Dav, "/dav/Projects/Contracts/"));
    }

    [Fact]
    public async Task Members_see_only_what_they_may_read()
    {
        var library = await SetupAsync("dav-permissions");
        var hidden = await FolderAsync(library, "HR");
        await UploadAsync(library, Pdf("salaries"), "s.pdf", "Salaries", hidden);
        await UploadAsync(library, Pdf("public"), "p.pdf", "Public");
        var broken = await library.Admin.PostAsJsonAsync($"{library.Url}/items/{hidden}/permissions/breakInheritance", new { copyGrants = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, broken.StatusCode);
        foreach (var (name, member) in new[] { ("dora", true), ("otto", false) })
        {
            var user = await library.Admin.PostAsJsonAsync("/v1.0/users", new { userName = name, password = $"{name}-password-1" }, Ct);
            if (member)
            {
                var id = (await user.ReadJsonAsync()).GetProperty("id").GetGuid();
                await library.Admin.PostAsJsonAsync($"/v1.0/workspaces/{library.Workspace}/members", new { userId = id, role = "member" }, Ct);
            }
        }

        var dora = await DavClientAsync(await ApiClient.CreateAsync(factory, library.Tenant, "dora", "dora-password-1"), library.Tenant, ReadScopes);
        Assert.Equal(["Public.pdf"], await NamesAsync(dora, "/dav/Projects/Contracts/"));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(dora, "PROPFIND", "/dav/Projects/Contracts/HR/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dora.GetAsync("/dav/Projects/Contracts/HR/Salaries.pdf", Ct)).StatusCode);

        var otto = await DavClientAsync(await ApiClient.CreateAsync(factory, library.Tenant, "otto", "otto-password-1"), library.Tenant, ReadScopes);
        Assert.Equal(["Home"], await NamesAsync(otto, "/dav/"));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(otto, "PROPFIND", "/dav/Projects/")).StatusCode);
    }

    [Fact]
    public async Task Tokens_only_work_in_their_tenant()
    {
        var library = await SetupAsync("dav-tenant-a");
        await factory.CreateTenantAsync("dav-tenant-b");
        await UploadAsync(library, Pdf("a"), "a.pdf", "Secret");
        var token = library.Dav.DefaultRequestHeaders.Authorization!.Parameter!;
        var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Tenant", "dav-tenant-b");
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);

        var response = await SendAsync(other, "PROPFIND", "/dav/");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/dav/Projects/Contracts/Secret.pdf", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_api_tells_a_library_its_webdav_address()
    {
        var library = await SetupAsync("dav-discovery");
        var second = await library.Admin.CreateWorkspaceAsync("Projects");
        var created = await library.Admin.PostAsJsonAsync($"/v1.0/workspaces/{second}/lists", new { name = "Contracts", templateKey = "documents" }, Ct);
        var secondList = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        var type = await library.Admin.CreateContentTypeAsync("Task", []);
        var tasks = await library.Admin.CreateListAsync(library.Workspace, "Tasks", type);

        var first = await (await library.Admin.GetAsync($"{library.Url}/webDav", Ct)).ReadJsonAsync();
        var duplicate = await (await library.Admin.GetAsync($"/v1.0/workspaces/{second}/lists/{secondList}/webDav", Ct)).ReadJsonAsync();

        Assert.EndsWith("/dav/Projects/Contracts/", first.GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/dav/Projects%20%282%29/Contracts/", duplicate.GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await library.Admin.GetAsync($"/v1.0/workspaces/{library.Workspace}/lists/{tasks}/webDav", Ct)).StatusCode);
        var path = new Uri(duplicate.GetProperty("url").GetString()!).AbsolutePath;
        Assert.Equal((HttpStatusCode)207, (await SendAsync(library.Dav, "PROPFIND", path)).StatusCode);
    }
}
