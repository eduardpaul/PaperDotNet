using Microsoft.OData.UriParser;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Lists.Fields;
using PaperDotNet.Taxonomy.Contracts;

namespace PaperDotNet.Lists.Querying;

/// <summary>
/// Filtering on a term of a managed metadata field also matches its child terms (TAX-03): the term ids in the filters
/// are looked up once, before the query is translated, and the translator compares with the whole subtree.
/// </summary>
internal static class TermHierarchy
{
    public static async Task<ParsedItemQuery> ExpandAsync(
        ParsedItemQuery parsed, IReadOnlyDictionary<string, FieldDefinition> fields, ITermStore terms, Guid tenantId, CancellationToken cancellationToken)
    {
        if (parsed.Filters.Count == 0)
        {
            return parsed;
        }

        var termFields = fields.Values.Where(f => f.Type == ManagedMetadataFieldType.TypeName).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        if (termFields.Count == 0)
        {
            return parsed;
        }

        var ids = new HashSet<Guid>();
        foreach (var (clause, _) in parsed.Filters)
        {
            Collect(clause.Expression, ids);
        }

        return ids.Count == 0 ? parsed : parsed with { TermFields = termFields, TermDescendants = await terms.GetDescendantsAsync(tenantId, ids, cancellationToken) };
    }

    /// <summary>The GUID literals of a filter.</summary>
    private static void Collect(QueryNode? node, HashSet<Guid> ids)
    {
        switch (node)
        {
            case ConstantNode { Value: Guid id }:
                ids.Add(id);
                break;
            case ConvertNode convert:
                Collect(convert.Source, ids);
                break;
            case BinaryOperatorNode binary:
                Collect(binary.Left, ids);
                Collect(binary.Right, ids);
                break;
            case UnaryOperatorNode unary:
                Collect(unary.Operand, ids);
                break;
            case AnyNode any:
                Collect(any.Body, ids);
                break;
            case InNode inNode when inNode.Right is CollectionConstantNode values:
                foreach (var value in values.Items)
                {
                    Collect(value, ids);
                }

                break;
        }
    }
}
