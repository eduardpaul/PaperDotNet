using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PaperDotNet.Abstractions;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Taxonomy.Contracts;

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
    Guid? TermSetId = null)
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
        CurrencyCode = CurrencyCode,
        DefaultValue = DefaultValue is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } d ? d.GetRawText() : null,
    };

    internal static FieldDefinitionDto From(FieldDefinition f) => new(
        f.Name, f.DisplayName, f.Type, f.Description, f.Required, f.AllowMultiple, f.MaxLength, f.Minimum, f.Maximum,
        f.Choices.Count > 0 ? f.Choices : null, f.LookupListId, f.CurrencyCode,
        f.DefaultValue is null ? null : JsonDocument.Parse(f.DefaultValue).RootElement.Clone(),
        f.TermSetId);
}

public sealed record ContentTypeResponse(Guid Id, string Name, string? Description, bool IsBuiltIn, IReadOnlyList<FieldDefinitionDto> Fields);

public sealed record ContentTypeRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(2000)] string? Description,
    IReadOnlyList<FieldDefinitionDto>? Fields);

public sealed record FieldTypeResponse(string Name, bool SupportsMultiple);

internal static class ContentTypeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapV1Group("contentTypes", "Content types");
        group.MapGet("", ListAsync).RequireScope(ListScopes.ContentTypeRead).WithName("ListContentTypes");
        group.MapGet("/{id:guid}", GetAsync).RequireScope(ListScopes.ContentTypeRead).WithName("GetContentType");
        group.MapPost("", CreateAsync).RequireScope(ListScopes.ContentTypeManage).WithName("CreateContentType");
        group.MapPut("/{id:guid}", ReplaceAsync).RequireScope(ListScopes.ContentTypeManage).WithName("ReplaceContentType");

        endpoints.MapV1Group("fieldTypes", "Content types")
            .MapGet("", (FieldTypeRegistry registry) => TypedResults.Ok(
                registry.All.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new FieldTypeResponse(t.Name, t.SupportsMultiple)).ToList()))
            .WithName("ListFieldTypes");
    }

    private static async Task<Ok<List<ContentTypeResponse>>> ListAsync(ListsDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.ContentTypes.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<Results<Ok<ContentTypeResponse>, ProblemHttpResult>> GetAsync(Guid id, ListsDbContext db, HttpResponse response, CancellationToken ct)
    {
        var contentType = await db.ContentTypes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contentType is null)
        {
            return ApiErrors.NotFound();
        }

        ETags.Set(response, contentType.Version);
        return TypedResults.Ok(ToResponse(contentType));
    }

    private static async Task<Results<Created<ContentTypeResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        ContentTypeRequest request, ListsDbContext db, FieldTypeRegistry registry, ITermStore terms, HttpResponse response, CancellationToken ct)
    {
        if (await ValidateAsync(request, db, registry, terms, null, ct) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (await db.ContentTypes.AnyAsync(c => c.Name == name, ct))
        {
            return ApiErrors.Conflict("nameAlreadyExists", $"A content type named '{name}' already exists.");
        }

        var contentType = new ContentType
        {
            Id = Ids.New(),
            Name = name,
            Description = request.Description,
            Fields = request.Fields?.Select(f => f.ToEntity()).ToList() ?? [],
        };
        db.ContentTypes.Add(contentType);
        await db.SaveChangesAsync(ct);
        ETags.Set(response, contentType.Version);
        return TypedResults.Created($"{ApiRoutes.V1}/contentTypes/{contentType.Id}", ToResponse(contentType));
    }

    /// <summary>
    /// Replaces name, description and fields. Existing fields keep their type and
    /// multiplicity (stored values must stay valid); new fields may be added.
    /// </summary>
    private static async Task<Results<Ok<ContentTypeResponse>, ValidationProblem, ProblemHttpResult>> ReplaceAsync(
        Guid id, ContentTypeRequest request, ListsDbContext db, FieldTypeRegistry registry, ITermStore terms, HttpRequest http, HttpResponse response, CancellationToken ct)
    {
        var contentType = await db.ContentTypes.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contentType is null)
        {
            return ApiErrors.NotFound();
        }

        if (!ETags.TryGetIfMatch(http, out var version))
        {
            return ApiErrors.PreconditionRequired();
        }

        if (version != contentType.Version)
        {
            return ApiErrors.PreconditionFailed();
        }

        if (await ValidateAsync(request, db, registry, terms, contentType, ct) is { } invalid)
        {
            return invalid;
        }

        var name = request.Name.Trim();
        if (name != contentType.Name && (contentType.IsBuiltIn || await db.ContentTypes.AnyAsync(c => c.Name == name, ct)))
        {
            return ApiErrors.Conflict("nameAlreadyExists", contentType.IsBuiltIn ? "Built-in content types cannot be renamed." : $"A content type named '{name}' already exists.");
        }

        contentType.Name = name;
        contentType.Description = request.Description;
        contentType.Fields = request.Fields?.Select(f => f.ToEntity()).ToList() ?? [];
        db.Entry(contentType).Property(c => c.Version).OriginalValue = version;
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

    private static async Task<ValidationProblem?> ValidateAsync(
        ContentTypeRequest request, ListsDbContext db, FieldTypeRegistry registry, ITermStore terms, ContentType? existing, CancellationToken ct)
    {
        if (RequestValidation.Validate(request) is { } invalid)
        {
            return invalid;
        }

        var fields = request.Fields?.Select(f => f.ToEntity()).ToList() ?? [];
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

        foreach (var lookup in fields.Where(f => f.LookupListId is not null).Select(f => f.LookupListId!.Value).Distinct())
        {
            if (!await db.Lists.AnyAsync(l => l.Id == lookup, ct))
            {
                errors.Add($"Lookup list {lookup} does not exist.");
            }
        }

        foreach (var termSetId in fields.Where(f => f.TermSetId is not null).Select(f => f.TermSetId!.Value).Distinct())
        {
            if (await terms.GetTermSetAsync(termSetId, ct) is null)
            {
                errors.Add($"Term set {termSetId} does not exist.");
            }
        }

        if (existing is not null)
        {
            foreach (var field in fields)
            {
                var old = existing.Fields.FirstOrDefault(f => f.Name == field.Name);
                if (old is not null && (old.Type != field.Type || old.AllowMultiple != field.AllowMultiple || old.TermSetId != field.TermSetId))
                {
                    errors.Add($"Field '{field.Name}': type, allowMultiple and termSetId cannot change once created.");
                }
            }
        }

        return errors.Count == 0 ? null : ApiErrors.Validation(new Dictionary<string, string[]> { ["fields"] = [.. errors] });
    }

    internal static ContentTypeResponse ToResponse(ContentType c) =>
        new(c.Id, c.Name, c.Description, c.IsBuiltIn, c.Fields.Select(FieldDefinitionDto.From).ToList());
}
