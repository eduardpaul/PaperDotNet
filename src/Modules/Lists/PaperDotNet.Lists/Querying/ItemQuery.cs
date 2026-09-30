using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using PaperDotNet.Api;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists.Querying;

/// <summary>
/// A parsed item query: <c>$filter</c> and <c>$orderby</c> in OData syntax (the standard the SDKs already speak),
/// validated against the list's fields by the OData URI parser. Microsoft.OData.Core's parser runs under Native AOT;
/// the parsed tree is translated to SQL by the provider (<see cref="IItemQueries"/>), not to LINQ: EF Core cannot run
/// dynamic LINQ under AOT (ADR-0039).
/// </summary>
/// <param name="TenantId"></param>
/// <param name="ListId"></param>
/// <param name="Filter"></param>
/// <param name="OrderBy"></param>
/// <param name="Page"></param>
/// <param name="Count"></param>
/// <param name="ItemId">Only this item (e.g. to test a workflow condition on it).</param>
internal sealed record ItemQuery(Guid TenantId, Guid ListId, FilterClause? Filter, OrderByClause? OrderBy, PageRequest Page, bool Count, Guid? ItemId = null);

internal sealed record ItemQueryResult(IReadOnlyList<ListItem> Items, long? Count);

/// <summary>Runs item queries; one implementation per database provider.</summary>
internal interface IItemQueries
{
    Task<ItemQueryResult> QueryAsync(ItemQuery query, CancellationToken cancellationToken);
}

internal static class ItemQueryParser
{
    public const string Namespace = "PaperDotNet";

    /// <summary>Top-level item properties and their EDM types.</summary>
    public static readonly IReadOnlyDictionary<string, EdmPrimitiveTypeKind> ItemProperties = new Dictionary<string, EdmPrimitiveTypeKind>(StringComparer.Ordinal)
    {
        ["id"] = EdmPrimitiveTypeKind.Guid,
        ["createdAt"] = EdmPrimitiveTypeKind.DateTimeOffset,
        ["updatedAt"] = EdmPrimitiveTypeKind.DateTimeOffset,
        ["createdBy"] = EdmPrimitiveTypeKind.Guid,
        ["updatedBy"] = EdmPrimitiveTypeKind.Guid,
    };

    /// <summary>Parses <c>$filter</c> and <c>$orderby</c>; the error is a message for a 400.</summary>
    public static (FilterClause? Filter, OrderByClause? OrderBy, string? Error) Parse(string? filter, string? orderBy, IReadOnlyList<FieldDefinition> fields)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            options["$filter"] = filter;
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            options["$orderby"] = orderBy;
        }

        if (options.Count == 0)
        {
            return (null, null, null);
        }

        var (model, itemType, items) = BuildModel(fields);
        try
        {
            var parser = new ODataQueryOptionParser(model, itemType, items, options);
            return (parser.ParseFilter(), parser.ParseOrderBy(), null);
        }
        catch (ODataException exception)
        {
            return (null, null, exception.Message);
        }
    }

    /// <summary>The EDM type of a field's values in queries.</summary>
    public static EdmPrimitiveTypeKind KindOf(string fieldType) => fieldType switch
    {
        FieldTypes.Number => EdmPrimitiveTypeKind.Double,
        FieldTypes.Boolean => EdmPrimitiveTypeKind.Boolean,
        FieldTypes.DateTime => EdmPrimitiveTypeKind.DateTimeOffset,
        _ => EdmPrimitiveTypeKind.String,
    };

    private static (EdmModel Model, EdmEntityType ItemType, EdmEntitySet Items) BuildModel(IReadOnlyList<FieldDefinition> fields)
    {
        var model = new EdmModel();
        var fieldsType = new EdmComplexType(Namespace, "Fields");
        fieldsType.AddStructuralProperty(FieldValues.Title, EdmPrimitiveTypeKind.String);
        foreach (var field in fields)
        {
            fieldsType.AddStructuralProperty(field.Name, EdmCoreModel.Instance.GetPrimitive(KindOf(field.Type), isNullable: true));
        }

        model.AddElement(fieldsType);
        var itemType = new EdmEntityType(Namespace, "Item");
        foreach (var (name, kind) in ItemProperties)
        {
            var property = itemType.AddStructuralProperty(name, EdmCoreModel.Instance.GetPrimitive(kind, isNullable: name is "createdBy" or "updatedBy"));
            if (name == "id")
            {
                itemType.AddKeys(property);
            }
        }

        itemType.AddStructuralProperty("fields", new EdmComplexTypeReference(fieldsType, isNullable: false));
        model.AddElement(itemType);
        var container = new EdmEntityContainer(Namespace, "Container");
        model.AddElement(container);
        var items = container.AddEntitySet("items", itemType);
        return (model, itemType, items);
    }
}
