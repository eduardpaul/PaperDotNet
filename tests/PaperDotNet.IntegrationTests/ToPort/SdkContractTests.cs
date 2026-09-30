using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace PaperDotNet.IntegrationTests;

/// <summary>What generated SDKs rely on (ADR-0032): ETags in bodies, one problem shape, query options, CORS.</summary>
public sealed class SdkContractTests(PaperDotNetApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bodies_carry_the_etag_of_the_header_and_lists_carry_one_per_entry()
    {
        await factory.CreateTenantAsync("sdk-etags");
        var client = await ApiClient.CreateAsync(factory, "sdk-etags");
        var ws = await client.CreateWorkspaceAsync("ETags");
        var workspace = await client.GetAsync($"/v1.0/workspaces/{ws}", Ct);
        Assert.Equal(workspace.Headers.ETag!.Tag, (await workspace.ReadJsonAsync()).GetProperty("@odata.etag").GetString());

        var list = await client.CreateListAsync(ws, "Notes");
        var created = await client.PostAsJsonAsync($"/v1.0/workspaces/{ws}/lists/{list}/items", new { fields = new { title = "One" } }, Ct);
        var etag = (await created.ReadJsonAsync()).GetProperty("@odata.etag").GetString();
        Assert.Equal(created.Headers.ETag!.Tag, etag);
        var page = await (await client.GetAsync($"/v1.0/workspaces/{ws}/lists/{list}/items", Ct)).ReadJsonAsync();
        Assert.Equal(etag, page.GetProperty("value")[0].GetProperty("@odata.etag").GetString());

        var group = await (await client.PostAsJsonAsync("/v1.0/groups", new { name = "G" }, Ct)).ReadJsonAsync();
        Assert.StartsWith("\"", group.GetProperty("@odata.etag").GetString(), StringComparison.Ordinal);
        var groups = await (await client.GetAsync("/v1.0/groups", Ct)).ReadJsonAsync();
        Assert.All(groups.GetProperty("value").EnumerateArray(), g => Assert.True(g.TryGetProperty("@odata.etag", out _)));
    }

    [Fact]
    public void No_endpoint_binds_an_enum_from_the_query_string()
    {
        // Minimal APIs parse query enums case-sensitively ("Pending"), but the API documents camelCase ("pending"):
        // such parameters are strings parsed with EnumQuery and documented with WithQueryEnum<T>.
        static bool IsEnum(Type type) => (Nullable.GetUnderlyingType(type) ?? type).IsEnum;
        var offenders = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => e.Metadata.OfType<MethodInfo>().Take(1).SelectMany(m => m.GetParameters())
                .Where(p => IsEnum(p.ParameterType) && p.GetCustomAttribute<FromBodyAttribute>() is null
                    && !e.RoutePattern.Parameters.Any(r => r.Name == p.Name))
                .Select(p => $"{e.RoutePattern.RawText} ({p.Name})"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Errors_are_problems_with_a_code()
    {
        await factory.CreateTenantAsync("sdk-problems");
        var client = await ApiClient.CreateAsync(factory, "sdk-problems");
        var missing = await client.GetAsync($"/v1.0/workspaces/{Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.True((await missing.ReadJsonAsync()).TryGetProperty("code", out _));
    }

    [Fact]
    public async Task The_openapi_document_describes_what_sdks_need()
    {
        var document = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json", Ct)).RootElement;
        var paths = document.GetProperty("paths");
        var items = paths.GetProperty("/v1.0/workspaces/{workspaceId}/lists/{listId}/items").GetProperty("get");
        var parameters = items.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("$filter", parameters);
        Assert.Contains("$orderby", parameters);
        Assert.Contains("$skiptoken", parameters);
        Assert.True(items.GetProperty("responses").TryGetProperty("4XX", out _));
        var upload = paths.GetProperty("/v1.0/workspaces/{workspaceId}/lists/{listId}/documents").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        Assert.Equal("binary", upload.GetProperty("properties").GetProperty("file").GetProperty("format").GetString());
        Assert.True(paths.GetProperty("/v1.0/me/events").GetProperty("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").TryGetProperty("text/event-stream", out _));
        Assert.Equal("object", document.GetProperty("components").GetProperty("schemas").GetProperty("JsonObject").GetProperty("type").GetString());
    }

    /// <summary>Shapes that make Kiota generate unusable code; each was a real bug once.</summary>
    [Fact]
    public async Task The_openapi_document_has_no_shapes_that_break_generators()
    {
        var document = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json", Ct)).RootElement;
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var paged = schemas.EnumerateObject()
            .Where(s => s.Value.TryGetProperty("properties", out var properties) && properties.TryGetProperty("@odata.nextLink", out _))
            .Select(s => "#/components/schemas/" + s.Name)
            .ToHashSet();
        var problems = new List<string>();
        var parameterNames = new Dictionary<string, string>();
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            // Sibling routes must name a segment's parameter alike, or Kiota builds templates like {%2Did}.
            var segments = path.Name.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i].StartsWith('{'))
                {
                    var prefix = string.Join('/', segments[..i]);
                    if (parameterNames.TryGetValue(prefix, out var name) && name != segments[i])
                    {
                        problems.Add($"{prefix}/: {name} and {segments[i]}");
                    }

                    parameterNames[prefix] = segments[i];
                }
            }

            foreach (var operation in path.Value.EnumerateObject().Where(o => o.Value.ValueKind == JsonValueKind.Object && o.Value.TryGetProperty("responses", out _)))
            {
                var result = operation.Value.GetProperty("responses").TryGetProperty("200", out var ok)
                    && ok.TryGetProperty("content", out var content) && content.TryGetProperty("application/json", out var json)
                    && json.GetProperty("schema").TryGetProperty("$ref", out var reference) ? reference.GetString() : null;
                var parameters = operation.Value.TryGetProperty("parameters", out var list)
                    ? list.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList()
                    : [];
                if (result is not null && paged.Contains(result) && !parameters.Contains("$skiptoken") && !parameters.Contains("$skip"))
                {
                    problems.Add($"{operation.Name} {path.Name}: paged without $skiptoken or $skip");
                }
            }
        }

        // Numbers must not also be strings (generators then give up on the type).
        problems.AddRange(Walk(document).Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array
                && type.EnumerateArray().Any(t => t.GetString() == "string") && type.EnumerateArray().Any(t => t.GetString() is "integer" or "number"))
            .Select(e => $"number as string: {e}"));
        Assert.False(schemas.TryGetProperty("JsonElement", out _), "JsonElement must be inlined as any JSON.");
        Assert.Empty(problems);
    }

    private static IEnumerable<JsonElement> Walk(JsonElement element)
    {
        yield return element;
        var children = element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Select(p => p.Value),
            JsonValueKind.Array => element.EnumerateArray(),
            _ => [],
        };
        foreach (var child in children.SelectMany(Walk))
        {
            yield return child;
        }
    }
}
