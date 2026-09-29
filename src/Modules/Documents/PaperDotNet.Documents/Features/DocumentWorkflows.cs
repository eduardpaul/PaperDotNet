using System.Text.Json.Nodes;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// Built-in workflows of documents (EVT-12, ADR-0036): classifying and reading new documents with AI once their text is
/// extracted (<c>document.processed</c>). Offered when a chat model is configured; turned on per workspace.
/// </summary>
internal static class DocumentWorkflows
{
    public const string Classify = "documents.classify";
    public const string Extract = "documents.extract";

    private const string Common = """
        "library": { "type": "string", "description": "Only documents of this library (default: all libraries of the workspace)." },
        "execution": { "type": "string", "description": "immediate or batch (AI calls in the organization's batch window)." }
        """;

    public static readonly BuiltInWorkflow[] All =
    [
        new(Classify, "Classify new documents", "Picks the term of a term set that fits each new document best and sets it in a field.",
            JsonNode.Parse("""
                {
                  "trigger": { "type": "document.processed", "list": "{param:library}" },
                  "flow": {
                    "start": "classify",
                    "nodes": {
                      "classify": { "activity": "ai.classify",
                                    "inputs": { "termSet": "{param:termSet}", "field": "{param:field}", "minConfidence": "{param:minConfidence}", "execution": "{param:execution}" } }
                    }
                  }
                }
                """)!.AsObject())
        {
            Parameters = JsonNode.Parse($$"""
                {
                  "type": "object",
                  "properties": {
                    "termSet": { "type": "string", "description": "The term set to classify with, as Group/Set." },
                    "field": { "type": "string", "description": "The managed metadata field to set." },
                    "minConfidence": { "type": "number", "default": 0.7, "description": "Less confidence (0 to 1) is not applied." },
                    {{Common}}
                  },
                  "required": ["termSet", "field"]
                }
                """)!.AsObject(),
            Requires = BuiltInRequirements.Ai,
        },
        new(Extract, "Extract fields", "Fills the fields of each new document from its text (e.g. a receipt's store, date and total).",
            JsonNode.Parse("""
                {
                  "trigger": { "type": "document.processed", "list": "{param:library}" },
                  "flow": {
                    "start": "extract",
                    "nodes": {
                      "extract": { "activity": "ai.extract",
                                   "inputs": { "fields": "{param:fields}", "minConfidence": "{param:minConfidence}", "execution": "{param:execution}" } }
                    }
                  }
                }
                """)!.AsObject())
        {
            Parameters = JsonNode.Parse($$"""
                {
                  "type": "object",
                  "properties": {
                    "fields": { "type": "array", "description": "The fields to fill (default: all AI can fill)." },
                    "minConfidence": { "type": "number", "default": 0.7, "description": "Values with less confidence (0 to 1) are not applied." },
                    {{Common}}
                  }
                }
                """)!.AsObject(),
            Requires = BuiltInRequirements.Ai,
        },
    ];
}
