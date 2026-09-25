using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using PaperDotNet.Api;

namespace PaperDotNet.Host;

/// <summary>
/// Completes the OpenAPI description so generated SDKs (Kiota, API-03) need no hand-written calls: query options,
/// one typed error for every failure, documented raw-JSON bodies, files and event streams (ADR-0032).
/// </summary>
internal sealed class SdkOperationTransformer : IOpenApiOperationTransformer
{
    private static readonly (QueryOptions Option, string Name, JsonSchemaType Type, string Description)[] Options =
    [
        (QueryOptions.Filter, "$filter", JsonSchemaType.String, "OData filter, e.g. fields/amount gt 100 and fields/status eq 'open'."),
        (QueryOptions.OrderBy, "$orderby", JsonSchemaType.String, "OData order, e.g. fields/due desc."),
        (QueryOptions.Select, "$select", JsonSchemaType.String, "Comma-separated field names to return."),
        (QueryOptions.Top, "$top", JsonSchemaType.Integer, "Page size."),
        (QueryOptions.SkipToken, "$skiptoken", JsonSchemaType.String, "Continuation token from @odata.nextLink."),
        (QueryOptions.Count, "$count", JsonSchemaType.Boolean, "Include @odata.count."),
        (QueryOptions.DeltaToken, "$deltatoken", JsonSchemaType.String, "Token from @odata.deltaLink."),
    ];

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        AddQueryOptions(operation, context, metadata);

        if (metadata.OfType<RequestBodySchemaMetadata>().LastOrDefault() is { } body)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new() { Schema = await SchemaAsync(body.Type, context, cancellationToken) },
                },
            };
        }

        FlattenMultipart(operation);
        operation.Responses ??= [];
        if (metadata.OfType<BinaryResponseMetadata>().LastOrDefault() is { } binary)
        {
            operation.Responses["200"] = new OpenApiResponse
            {
                Description = "The file.",
                Content = binary.ContentTypes.ToDictionary(t => t, _ => new OpenApiMediaType { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }),
            };
        }

        if (metadata.OfType<EventStreamMetadata>().Any())
        {
            operation.Responses["200"] = new OpenApiResponse
            {
                Description = "Server-sent events.",
                Content = new Dictionary<string, OpenApiMediaType> { ["text/event-stream"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String } } },
            };
        }

        // Every failure is an ApiProblem: specific codes keep their description, ranges cover the rest.
        var problem = await SchemaAsync(typeof(ApiProblem), context, cancellationToken);
        foreach (var (code, response) in operation.Responses.Where(r => r.Key.StartsWith('4') || r.Key.StartsWith('5')).ToList())
        {
            operation.Responses[code] = new OpenApiResponse { Description = response.Description ?? "Error", Content = ProblemContent(problem) };
        }

        operation.Responses.TryAdd("4XX", new OpenApiResponse { Description = "Client error", Content = ProblemContent(problem) });
        operation.Responses.TryAdd("5XX", new OpenApiResponse { Description = "Server error", Content = ProblemContent(problem) });
    }

    private static void AddQueryOptions(OpenApiOperation operation, OpenApiOperationTransformerContext context, IList<object> metadata)
    {
        var options = metadata.OfType<QueryOptionsMetadata>().LastOrDefault()?.Options ?? QueryOptions.None;
        var result = context.Description.SupportedResponseTypes.FirstOrDefault(r => r.StatusCode == 200)?.Type;
        if (result is { IsGenericType: true } && result.GetGenericTypeDefinition() == typeof(Page<>))
        {
            options |= QueryOptions.Paging;
        }

        if (options == QueryOptions.None)
        {
            return;
        }

        operation.Parameters ??= [];
        foreach (var (option, name, type, description) in Options)
        {
            if (options.HasFlag(option) && !operation.Parameters.Any(p => p.Name == name))
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = name,
                    In = ParameterLocation.Query,
                    Description = description,
                    Schema = new OpenApiSchema { Type = type },
                });
            }
        }
    }

    /// <summary>
    /// Form uploads are described by ASP.NET as <c>allOf</c> fragments with a reference to <c>IFormFile</c>; Kiota only
    /// generates its multipart body when the form is one object with inline binary properties.
    /// </summary>
    private static void FlattenMultipart(OpenApiOperation operation)
    {
        if (operation.RequestBody?.Content?.TryGetValue("multipart/form-data", out var media) != true || media!.Schema is not OpenApiSchema form)
        {
            return;
        }

        var properties = new Dictionary<string, IOpenApiSchema>();
        foreach (var part in (form.AllOf ?? []).Append(form))
        {
            foreach (var (name, property) in part.Properties ?? new Dictionary<string, IOpenApiSchema>())
            {
                properties[name] = property is OpenApiSchemaReference { Reference.Id: "IFormFile" or "IFormFileCollection" }
                    ? new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                    : property;
            }
        }

        media.Schema = new OpenApiSchema { Type = JsonSchemaType.Object, Properties = properties };
    }

    private static Dictionary<string, OpenApiMediaType> ProblemContent(IOpenApiSchema problem) =>
        new() { ["application/problem+json"] = new() { Schema = problem } };

    /// <summary>A component schema for <paramref name="type"/>, referenced by name.</summary>
    private static async Task<IOpenApiSchema> SchemaAsync(Type type, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var schema = await context.GetOrCreateSchemaAsync(type, null, ct);
        var document = context.Document!;
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas.TryAdd(type.Name, schema);
        return new OpenApiSchemaReference(type.Name, document);
    }
}

/// <summary>
/// <c>JsonObject</c> values (item fields, settings) are objects with any properties, so SDKs give them a dictionary
/// instead of an untyped node; <c>JsonArray</c> is an array of anything.
/// </summary>
internal sealed class SdkSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(JsonObject))
        {
            schema.Type = JsonSchemaType.Object;
            schema.AdditionalPropertiesAllowed = true;
            schema.AdditionalProperties = new OpenApiSchema();
        }
        else if (type == typeof(JsonArray))
        {
            schema.Type = JsonSchemaType.Array;
            schema.Items = new OpenApiSchema();
        }

        return Task.CompletedTask;
    }
}
