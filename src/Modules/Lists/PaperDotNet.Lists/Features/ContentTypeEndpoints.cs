using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists.Features;

public sealed record FieldDefinitionDto(
    string Name,
    string? DisplayName,
    string Type,
    string? Description = null,
    bool Required = false,
    bool AllowMultiple = false,
    int? MaxLength = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    IReadOnlyList<string>? Choices = null,
    Guid? LookupListId = null,
    string? CurrencyCode = null,
    JsonElement? DefaultValue = null,
    Guid? TermSetId = null,
    FieldSearchWeight? Search = null,
    bool Indexed = false)
{
    internal FieldDefinition ToEntity() => new()
    {
        Name = Name?.Trim() ?? string.Empty,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Name ?? string.Empty : DisplayName.Trim(),
        Type = Type ?? string.Empty,
        Description = Description,
        Required = Required,
        AllowMultiple = AllowMultiple,
        MaxLength = MaxLength,
        Minimum = Minimum,
        Maximum = Maximum,
        Choices = Choices?.ToList() ?? [],
        LookupListId = LookupListId,
        TermSetId = TermSetId,
        Search = Search,
        Indexed = Indexed,
        CurrencyCode = CurrencyCode,
        DefaultValue = DefaultValue is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } d ? d.GetRawText() : null,
    };

    internal static FieldDefinitionDto From(FieldDefinition f) => new(
        f.Name, f.DisplayName, f.Type, f.Description, f.Required, f.AllowMultiple, f.MaxLength, f.Minimum, f.Maximum,
        f.Choices.Count > 0 ? f.Choices : null, f.LookupListId, f.CurrencyCode,
        f.DefaultValue is null ? null : JsonElement.Parse(f.DefaultValue),
        f.TermSetId,
        f.Search,
        f.Indexed);
}

/// <summary>A content type. <c>key</c> is set when it comes from a template; <c>extensionId</c> when an extension manages it (read-only).</summary>
public sealed record ContentTypeResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsBuiltIn,
    string? Key,
    string? ExtensionId,
    IReadOnlyList<FieldDefinitionDto> Fields,
    [property: JsonPropertyName("@odata.etag")] string ETag);

public sealed record ContentTypeRequest(string? Name, string? Description, IReadOnlyList<FieldDefinitionDto>? Fields);

public sealed record FieldTypeResponse(string Name, bool SupportsMultiple);

/// <summary>Content types (tenant-wide schemas) and the field type catalog.</summary>
internal static class ContentTypeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1.0/contentTypes").WithTags("Content types");
        group.MapGet("", ListAsync).RequireScope(ListScopes.ContentTypeRead).WithName("ListContentTypes");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(ListScopes.ContentTypeRead).WithName("GetContentType");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.ContentTypeManage).WithName("CreateContentType");
        group.MapPut("/{id:guid}", ReplaceAsync).RequireScope(ListScopes.ContentTypeManage).WithName("ReplaceContentType");

        app.MapGet("/v1.0/fieldTypes", (FieldTypeRegistry registry) => TypedResults.Ok(
                registry.All.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new FieldTypeResponse(t.Name, t.SupportsMultiple)).ToList()))
            .RequireAuthorization().WithTags("Content types").WithName("ListFieldTypes");
    }

    private static async Task<Ok<List<ContentTypeResponse>>> ListAsync(Caller caller, ListsDbContext database, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var ct = cancellationToken;
        var contentTypes = await db.ContentTypes.AsNoTracking().Where(c => c.TenantId == tenant).OrderBy(c => c.Name).ToListAsync(ct);
        return TypedResults.Ok(contentTypes.Select(ToResponse).ToList());
    }

    private static async Task<Results<Ok<ContentTypeResponse>, ProblemHttpResult>> GetAsync(Guid id, Caller caller, ListSchemaLoader loader, HttpResponse response, CancellationToken cancellationToken)
    {
        if (await loader.FindContentTypeAsync(caller.TenantId, id, cancellationToken) is not { } contentType)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, contentType.Version);
        return TypedResults.Ok(ToResponse(contentType));
    }

    private static async Task<Results<Created<ContentTypeResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        ContentTypeRequest request, Caller caller, ListsDbContext db, FieldTypeRegistry registry, IServiceProvider services, HttpResponse response, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(request, caller.TenantId, db, registry, services, null, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name!.Trim();
        if (await NameTakenAsync(db, caller.TenantId, name, cancellationToken))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A content type named '{name}' already exists.");
        }

        var contentType = new ContentType
        {
            Id = Ids.New(),
            TenantId = caller.TenantId,
            Name = name,
            Description = request.Description,
            Fields = ListsJsonText.Fields([.. request.Fields?.Select(f => f.ToEntity()) ?? []]),
        };
        db.ContentTypes.Add(contentType);
        await db.SaveChangesAsync(cancellationToken);
        ETags.Set(response, contentType.Version);
        return TypedResults.Created($"/v1.0/contentTypes/{contentType.Id}", ToResponse(contentType));
    }

    /// <summary>
    /// Replaces name, description and fields. Existing fields keep their type and multiplicity (stored values must
    /// stay valid); new fields may be added.
    /// </summary>
    private static async Task<Results<Ok<ContentTypeResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid id, ContentTypeRequest request, Caller caller, ListsDbContext database, FieldTypeRegistry registry, IServiceProvider services,
        HttpRequest http, HttpResponse response, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = caller.TenantId;
        var key = id;
        var ct = cancellationToken;
        var contentType = await db.ContentTypes.Where(c => c.TenantId == tenant && c.Id == key).FirstOrDefaultAsync(ct);
        if (contentType is null)
        {
            return ApiErrors.NotFound();
        }

        if (contentType.ExtensionId is not null)
        {
            return ApiErrors.Conflict("managedByExtension", $"The content type is managed by the extension '{contentType.ExtensionId}'.");
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (version != contentType.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (await ValidateAsync(request, tenant, db, registry, services, ListsJsonText.Fields(contentType.Fields), ct) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name!.Trim();
        if (name != contentType.Name && (contentType.IsBuiltIn || await NameTakenAsync(db, tenant, name, ct)))
        {
            return ApiErrors.Conflict("nameAlreadyExists", contentType.IsBuiltIn ? "Built-in content types cannot be renamed." : $"A content type named '{name}' already exists.");
        }

        contentType.Name = name;
        contentType.Description = request.Description;
        contentType.Fields = ListsJsonText.Fields([.. request.Fields?.Select(f => f.ToEntity()) ?? []]);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiErrors.PreconditionFailed();
        }

        ETags.Set(response, contentType.Version);
        return TypedResults.Ok(ToResponse(contentType));
    }

    private static Task<bool> NameTakenAsync(ListsDbContext database, Guid tenantId, string name, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var contentTypeName = name;
        var ct = cancellationToken;
        return db.ContentTypes.AnyAsync(c => c.TenantId == tenant && c.Name == contentTypeName, ct);
    }

    private static async Task<ValidationProblem?> ValidateAsync(
        ContentTypeRequest request, Guid tenantId, ListsDbContext db, FieldTypeRegistry registry, IServiceProvider services,
        IReadOnlyList<FieldDefinition>? existing, CancellationToken cancellationToken)
    {
        if (request.Name?.Trim() is not { Length: > 0 and <= 200 })
        {
            return ApiErrors.Validation("name", "A name of 1 to 200 characters is required.");
        }

        if (request.Description is { Length: > 2000 })
        {
            return ApiErrors.Validation("description", "The description can have at most 2000 characters.");
        }

        var fields = request.Fields?.Select(f => f.ToEntity()).ToList() ?? [];
        var errors = FieldErrors(fields, registry);
        if (services.GetService<IFieldTypeAvailability>() is { } availability)
        {
            foreach (var type in fields.Select(f => f.Type).Where(t => registry.Find(t) is not null).Distinct(StringComparer.Ordinal))
            {
                if (!await availability.IsAvailableAsync(tenantId, type, cancellationToken))
                {
                    errors.Add($"Field type '{type}' is not enabled for this organization.");
                }
            }
        }

        foreach (var lookup in fields.Where(f => f.LookupListId is not null).Select(f => f.LookupListId!.Value).Distinct())
        {
            var context = db;
            var tenant = tenantId;
            var list = lookup;
            var ct = cancellationToken;
            if (!await context.Lists.AnyAsync(l => l.TenantId == tenant && l.Id == list && l.DeletedAt == null, ct))
            {
                errors.Add($"Lookup list {lookup} does not exist.");
            }
        }

        foreach (var field in fields)
        {
            var old = existing?.FirstOrDefault(f => f.Name == field.Name);
            if (old is not null && (old.Type != field.Type || old.AllowMultiple != field.AllowMultiple || old.TermSetId != field.TermSetId))
            {
                errors.Add($"Field '{field.Name}': type, allowMultiple and termSetId cannot change once created.");
            }
        }

        return errors.Count == 0 ? null : ApiErrors.Validation(new Dictionary<string, string[]> { ["fields"] = [.. errors] });
    }

    /// <summary>Problems in field definitions that need no lookups: types and settings, duplicates, default values.</summary>
    internal static List<string> FieldErrors(IReadOnlyList<FieldDefinition> fields, FieldTypeRegistry registry)
    {
        var errors = fields.SelectMany(registry.Validate).ToList();
        errors.AddRange(fields.GroupBy(f => f.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"Field '{g.Key}' is defined more than once."));
        foreach (var field in fields.Where(f => f.DefaultValue is not null))
        {
            try
            {
                using var _ = JsonDocument.Parse(field.DefaultValue!);
            }
            catch (JsonException)
            {
                errors.Add($"Field '{field.Name}': defaultValue is not valid JSON.");
            }
        }

        return errors;
    }

    internal static ContentTypeResponse ToResponse(ContentType c) =>
        new(c.Id, c.Name, c.Description, c.IsBuiltIn, c.Key, c.ExtensionId, [.. ListsJsonText.Fields(c.Fields).Select(FieldDefinitionDto.From)], ETags.From(c.Version));

    /// <summary>The built-in Item content type (title only), created on first use in a tenant.</summary>
    internal static async Task<Guid> EnsureItemContentTypeAsync(ListsDbContext database, Guid tenantId, CancellationToken cancellationToken)
    {
        var db = database;
        var tenant = tenantId;
        var name = ContentType.ItemName;
        var ct = cancellationToken;
        var id = await db.ContentTypes.Where(c => c.TenantId == tenant && c.IsBuiltIn && c.Name == name).Select(c => c.Id).FirstOrDefaultAsync(ct);
        if (id != Guid.Empty)
        {
            return id;
        }

        var item = new ContentType { Id = Ids.New(), TenantId = tenant, Name = name, Description = "A generic item with a title.", IsBuiltIn = true };
        db.ContentTypes.Add(item);
        await db.SaveChangesAsync(ct);
        return item.Id;
    }
}
