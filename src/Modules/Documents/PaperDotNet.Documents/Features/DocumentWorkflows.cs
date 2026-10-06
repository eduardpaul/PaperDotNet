using System.Text.Json.Nodes;
using PaperDotNet.Workflows.Contracts;

namespace PaperDotNet.Documents.Features;

/// <summary>
/// Built-in workflows of documents (EVT-12, ADR-0038). An upload only stores the file and raises <c>document.added</c>;
/// what happens next is these workflows, each turned on or off per library:
/// <list type="bullet">
/// <item>"Read the text" (<c>documents.text</c>, on): the PDF text layer as page texts for search; raises
/// <c>wf.documents.text.hasText</c> or <c>wf.documents.text.noText</c>.</item>
/// <item>"Make thumbnails" (<c>documents.thumbnail</c>, on) and "Render pages" (<c>documents.pages</c>, on): the images
/// the library shows; without them it shows none.</item>
/// <item>"Recognize text" (<c>documents.ocr</c>, off): OCR of scans and photos after <c>noText</c>.</item>
/// </list>
/// Each can also be started by hand on a document (to run it again). OCR's result is a new version, announced like any
/// other, so its text is read and its images made by the same workflows. The four are process roles of libraries
/// (ADR-0047): a library may replace one with a copy or an extension's workflow (e.g. text from an LLM or another OCR
/// engine, saved with <c>document.saveText</c>); a replacement raises the role's events, so search, OCR and AI follow it. Classifying and extracting with AI (turned on
/// per workspace, when a chat model is configured) follow the text: <c>wf.documents.text.hasText</c>.
/// </summary>
internal static class DocumentWorkflows
{
    public const string Text = "documents.text";
    public const string Thumbnail = "documents.thumbnail";
    public const string Pages = "documents.pages";
    public const string Ocr = "documents.ocr";
    public const string Classify = "documents.classify";
    public const string Extract = "documents.extract";

    /// <summary>A per-library workflow: on a new file of the library, and by hand on one of its documents.</summary>
    private static JsonObject OnNewFiles(string flow) => JsonNode.Parse($$"""
        {
          "triggers": [
            { "type": "{{WorkflowTriggers.DocumentAdded}}", "list": "{param:list}" },
            { "type": "{{WorkflowTriggers.Manual}}", "list": "{param:list}" }
          ],
          "flow": {{flow}}
        }
        """)!.AsObject();

    private const string Common = """
        "library": { "type": "string", "description": "Only documents of this library (default: all libraries of the workspace)." },
        "execution": { "type": "string", "description": "immediate or batch (AI calls in the organization's batch window)." }
        """;

    /// <summary>The AI workflows start once a document's text was read (also that of an OCR version).</summary>
    private const string AfterText = $$"""
        "trigger": { "type": "wf.{{Text}}.hasText", "list": "{param:library}" }
        """;

    public static readonly BuiltInWorkflow[] All =
    [
        new(Text, "Read the text", "Reads the text layer of new PDFs for search, and tells scans and photos apart (for OCR).",
            OnNewFiles("""
                {
                  "start": "read",
                  "nodes": {
                    "read": { "activity": "document.readText", "next": { "text": "has text", "noText": "no text" } },
                    "has text": { "activity": "event.raise", "inputs": { "event": "hasText" } },
                    "no text": { "activity": "event.raise", "inputs": { "event": "noText" } }
                  }
                }
                """))
        {
            Scope = BuiltInScope.Library,
            EnabledByDefault = true,
            System = true,
        },
        new(Thumbnail, "Make thumbnails", "Makes the thumbnail of each new file, for lists and grids.",
            OnNewFiles("""{ "start": "thumbnail", "nodes": { "thumbnail": { "activity": "document.thumbnail" } } }"""))
        {
            Scope = BuiltInScope.Library,
            EnabledByDefault = true,
            System = true,
        },
        new(Pages, "Render pages", "Renders the pages of each new file as images, for viewing documents.",
            OnNewFiles("""{ "start": "render", "nodes": { "render": { "activity": "document.renderPages" } } }"""))
        {
            Scope = BuiltInScope.Library,
            EnabledByDefault = true,
            System = true,
        },
        new(Ocr, "Recognize text", "Recognizes the text of scans and photos (OCR) as a searchable PDF version, after reading found no text.",
            JsonNode.Parse($$"""
                {
                  "triggers": [
                    { "type": "wf.{{Text}}.noText", "list": "{param:list}" },
                    {
                      "type": "{{WorkflowTriggers.Manual}}", "list": "{param:list}",
                      "inputs": { "properties": { "force": { "type": "boolean", "description": "Recognize the text even when the file has text." } } }
                    }
                  ],
                  "flow": { "start": "ocr", "nodes": { "ocr": { "activity": "document.ocr", "inputs": { "languages": "{param:languages}", "force": "{var:force}" } } } }
                }
                """)!.AsObject())
        {
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "languages": { "type": "string", "description": "Tesseract languages, e.g. deu+eng (default: the file's, the library's OCR languages, the uploader's)." }
                  }
                }
                """)!.AsObject(),
            Scope = BuiltInScope.Library,
            System = true,
        },
        new(Classify, "Classify new documents", "Picks the term of a term set that fits each new document best and sets it in a field.",
            JsonNode.Parse($$"""
                {
                  {{AfterText}},
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
            JsonNode.Parse($$"""
                {
                  {{AfterText}},
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
