using Microsoft.OData.Edm;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Data;
using PaperDotNet.Lists.Fields;

namespace PaperDotNet.Lists.Querying;

/// <summary>
/// OData (EDM) model of one list, built at runtime from its fields, so the OData
/// parser validates <c>$filter</c>/<c>$orderby</c> against the real schema.
/// Items look like Graph list items: top-level properties plus <c>fields/…</c>.
/// </summary>
internal sealed class ItemEdmModel
{
    public const string Namespace = "PaperDotNet";

    /// <summary>Top-level item properties and their EDM types.</summary>
    public static readonly IReadOnlyDictionary<string, EdmPrimitiveTypeKind> ItemProperties = new Dictionary<string, EdmPrimitiveTypeKind>(StringComparer.Ordinal)
    {
        ["id"] = EdmPrimitiveTypeKind.Guid,
        ["contentTypeId"] = EdmPrimitiveTypeKind.Guid,
        ["parentId"] = EdmPrimitiveTypeKind.Guid,
        ["isFolder"] = EdmPrimitiveTypeKind.Boolean,
        ["createdAt"] = EdmPrimitiveTypeKind.DateTimeOffset,
        ["updatedAt"] = EdmPrimitiveTypeKind.DateTimeOffset,
        ["createdBy"] = EdmPrimitiveTypeKind.Guid,
        ["updatedBy"] = EdmPrimitiveTypeKind.Guid,
    };

    private ItemEdmModel(IEdmModel model, IEdmEntityType itemType, IEdmEntitySet items, IReadOnlyDictionary<string, (FieldDefinition Field, IFieldType Type)> fields)
    {
        Model = model;
        ItemType = itemType;
        Items = items;
        Fields = fields;
    }

    public IEdmModel Model { get; }

    public IEdmEntityType ItemType { get; }

    public IEdmEntitySet Items { get; }

    /// <summary>Queryable fields (besides <c>title</c>) by name.</summary>
    public IReadOnlyDictionary<string, (FieldDefinition Field, IFieldType Type)> Fields { get; }

    public static ItemEdmModel Build(IReadOnlyDictionary<string, FieldDefinition> schemaFields, FieldTypeRegistry registry)
    {
        var model = new EdmModel();
        var fieldsType = new EdmComplexType(Namespace, "Fields");
        fieldsType.AddStructuralProperty("title", EdmPrimitiveTypeKind.String);

        var fields = new Dictionary<string, (FieldDefinition, IFieldType)>(StringComparer.Ordinal);
        foreach (var field in schemaFields.Values)
        {
            if (registry.Find(field.Type) is not { } type)
            {
                continue;
            }

            var primitive = EdmCoreModel.Instance.GetPrimitive(EdmKindOf(type.ValueKind), isNullable: true);
            IEdmTypeReference reference = field.AllowMultiple
                ? new EdmCollectionTypeReference(new EdmCollectionType(primitive))
                : primitive;
            fieldsType.AddStructuralProperty(field.Name, reference);
            fields[field.Name] = (field, type);
        }

        model.AddElement(fieldsType);

        var itemType = new EdmEntityType(Namespace, "Item");
        foreach (var (name, kind) in ItemProperties)
        {
            var property = itemType.AddStructuralProperty(name, EdmCoreModel.Instance.GetPrimitive(kind, isNullable: name is "parentId" or "createdBy" or "updatedBy"));
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
        return new ItemEdmModel(model, itemType, items, fields);
    }

    /// <summary>The OData type used to parse literals for a field value kind.</summary>
    internal static EdmPrimitiveTypeKind EdmKindOf(FieldValueKind kind) => kind switch
    {
        FieldValueKind.Number => EdmPrimitiveTypeKind.Decimal,
        FieldValueKind.Boolean => EdmPrimitiveTypeKind.Boolean,
        FieldValueKind.Date => EdmPrimitiveTypeKind.Date,
        FieldValueKind.DateTime => EdmPrimitiveTypeKind.DateTimeOffset,
        FieldValueKind.Identifier => EdmPrimitiveTypeKind.Guid,
        _ => EdmPrimitiveTypeKind.String,
    };
}
