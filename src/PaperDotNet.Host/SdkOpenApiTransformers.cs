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
        (QueryOptions.Skip, "$skip", JsonSchemaType.Integer, "Results to skip (use @odata.nextLink to page)."),
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
/// instead of an untyped node; <c>JsonArray</c> is an array of anything. Numbers are numbers: the API also reads them
/// from strings, which ASP.NET describes as <c>integer | string</c> with a pattern, and generators then give up on the type.
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

        if (schema.Type is { } kind && kind.HasFlag(JsonSchemaType.String) && (kind.HasFlag(JsonSchemaType.Integer) || kind.HasFlag(JsonSchemaType.Number)))
        {
            schema.Type = kind & ~JsonSchemaType.String;
            schema.Pattern = null;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// <c>JsonElement</c> values (operation results, run logs, batch bodies, WebAuthn options) can be any JSON. As a named
/// component SDK generators make an empty object model of it, which loses arrays and scalars; inlined as an empty
/// schema they get an untyped JSON value. Inlined <c>JsonObject</c> schemas point back to the component.
/// </summary>
internal sealed class SdkDocumentTransformer : IOpenApiDocumentTransformer
{
    private const string AnyJson = "JsonElement";
    private const string JsonObjectName = "JsonObject";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null)
        {
            return Task.CompletedTask;
        }

        schemas.Remove(AnyJson);
        var jsonObject = schemas.ContainsKey(JsonObjectName) ? new OpenApiSchemaReference(JsonObjectName, document) : null;

        foreach (var name in schemas.Keys.ToList())
        {
            schemas[name] = name == JsonObjectName ? schemas[name] : Rewrite(schemas[name], jsonObject);
        }

        foreach (var operation in (document.Paths ?? []).Values.SelectMany(p => p.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>()))
        {
            foreach (var media in (operation.RequestBody?.Content?.Values ?? []).Concat((operation.Responses ?? []).Values.SelectMany(r => r.Content?.Values ?? [])))
            {
                if (media.Schema is { } schema)
                {
                    media.Schema = Rewrite(schema, jsonObject);
                }
            }
        }

        RemoveUnused(document, schemas);
        return Task.CompletedTask;
    }

    /// <summary>Drops component schemas nothing refers to (e.g. result types replaced by files or event streams).</summary>
    private static void RemoveUnused(OpenApiDocument document, IDictionary<string, IOpenApiSchema> schemas)
    {
        var used = new HashSet<string>();
        var pending = new Stack<IOpenApiSchema>();
        foreach (var operation in (document.Paths ?? []).Values.SelectMany(p => p.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>()))
        {
            var media = (operation.RequestBody?.Content?.Values ?? []).Concat((operation.Responses ?? []).Values.SelectMany(r => r.Content?.Values ?? []));
            foreach (var schema in media.Select(m => m.Schema).Concat((operation.Parameters ?? []).Select(p => p.Schema)))
            {
                if (schema is not null)
                {
                    pending.Push(schema);
                }
            }
        }

        while (pending.TryPop(out var schema))
        {
            if (schema is OpenApiSchemaReference { Reference.Id: { } id })
            {
                if (used.Add(id) && schemas.TryGetValue(id, out var target))
                {
                    pending.Push(target);
                }

                continue;
            }

            var parts = (schema.Properties?.Values ?? []).Concat(schema.AllOf ?? []).Concat(schema.OneOf ?? []).Concat(schema.AnyOf ?? [])
                .Append(schema.Items).Append(schema.AdditionalProperties);
            foreach (var part in parts)
            {
                if (part is not null)
                {
                    pending.Push(part);
                }
            }
        }

        foreach (var name in schemas.Keys.Where(n => !used.Contains(n)).ToList())
        {
            schemas.Remove(name);
        }
    }

    private static IOpenApiSchema Rewrite(IOpenApiSchema schema, IOpenApiSchema? jsonObject)
    {
        if (IsAnyJson(schema))
        {
            return new OpenApiSchema();
        }

        if (schema is not OpenApiSchema inline)
        {
            return schema;
        }

        // An inlined JsonObject (documented bodies) would become one more model per property.
        if (jsonObject is not null && inline.Type == JsonSchemaType.Object && inline.Properties is null or { Count: 0 }
            && inline.AdditionalProperties is OpenApiSchema { Type: null, Properties: null or { Count: 0 } })
        {
            return jsonObject;
        }

        // A nullable reference (oneOf null + JsonElement) is any JSON as well.
        if (inline.OneOf?.Any(IsAnyJson) == true || inline.AnyOf?.Any(IsAnyJson) == true)
        {
            return new OpenApiSchema { Description = inline.Description };
        }

        foreach (var name in (inline.Properties?.Keys ?? []).ToList())
        {
            inline.Properties![name] = Rewrite(inline.Properties[name], jsonObject);
        }

        if (inline.Items is { } items)
        {
            inline.Items = Rewrite(items, jsonObject);
        }

        if (inline.AdditionalProperties is { } additional)
        {
            inline.AdditionalProperties = Rewrite(additional, jsonObject);
        }

        return inline;
    }

    private static bool IsAnyJson(IOpenApiSchema schema) => schema is OpenApiSchemaReference { Reference.Id: AnyJson };
}
