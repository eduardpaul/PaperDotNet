using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PaperDotNet.Api;

/// <summary>Query options an endpoint reads from the query string (described in OpenAPI so SDKs expose them).</summary>
[Flags]
public enum QueryOptions
{
    None = 0,
    Top = 1,
    SkipToken = 2,
    Filter = 4,
    OrderBy = 8,
    Select = 16,
    Count = 32,
    DeltaToken = 64,

    /// <summary>Offset paging (ranked results such as search).</summary>
    Skip = 128,

    /// <summary>Keyset paging (<see cref="Page"/> results get it automatically).</summary>
    Paging = Top | SkipToken,

    /// <summary>OData queries over list items.</summary>
    Items = Paging | Filter | OrderBy | Select | Count,

    /// <summary>Delta sync (API-05).</summary>
    Delta = Paging | DeltaToken,
}

/// <summary>Endpoint metadata: the query options the endpoint reads (see <see cref="QueryOptions"/>).</summary>
public sealed record QueryOptionsMetadata(QueryOptions Options);

/// <summary>Endpoint metadata: the documented request body when the handler reads raw JSON (e.g. merge patches).</summary>
public sealed record RequestBodySchemaMetadata(Type Type);

/// <summary>Endpoint metadata: a successful response is a file in one of these media types.</summary>
public sealed record BinaryResponseMetadata(IReadOnlyList<string> ContentTypes);

/// <summary>Endpoint metadata: the response is a server-sent event stream (<c>text/event-stream</c>).</summary>
public sealed record EventStreamMetadata;

/// <summary>
/// The problem body of every error response (RFC 9457 with PaperDotNet's <c>code</c> and validation <c>errors</c>);
/// documented once so SDKs throw one typed error.
/// </summary>
public sealed record ApiProblem(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Instance,
    string? Code,
    string? TraceId,
    IReadOnlyDictionary<string, string[]>? Errors);

public static class OpenApiEndpointExtensions
{
    /// <summary>Documents the query options the endpoint reads.</summary>
    public static TBuilder WithQueryOptions<TBuilder>(this TBuilder builder, QueryOptions options)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new QueryOptionsMetadata(options));

    /// <summary>Documents <typeparamref name="T"/> as the JSON request body (for handlers that bind raw JSON).</summary>
    public static TBuilder WithRequestBodySchema<T, TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequestBodySchemaMetadata(typeof(T)));

    /// <summary>Documents a file response in these media types (default <c>application/octet-stream</c>).</summary>
    public static TBuilder ProducesBinary<TBuilder>(this TBuilder builder, params string[] contentTypes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new BinaryResponseMetadata(contentTypes.Length == 0 ? ["application/octet-stream"] : contentTypes));

    /// <summary>Documents a server-sent event stream response.</summary>
    public static TBuilder ProducesEventStream<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new EventStreamMetadata());
}

/// <summary>Shorter form of <see cref="OpenApiEndpointExtensions.WithRequestBodySchema{T, TBuilder}"/> for route handlers.</summary>
public static class RouteHandlerOpenApiExtensions
{
    public static RouteHandlerBuilder WithRequestBodySchema<T>(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new RequestBodySchemaMetadata(typeof(T)));
}
