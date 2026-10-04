using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Extensions;
using PaperDotNet.Workflows.Contracts;

[assembly: PaperDotNetExtension(typeof(PaperDotNet.StorageOptimization.StorageOptimizationExtension))]

namespace PaperDotNet.StorageOptimization;

public sealed class StorageOptimizationExtension : IExtension
{
    public const string Id = "paperdotnet.storageoptimization";
    public const string ReviewType = Id + ".image";

    public void Configure(IExtensionBuilder builder)
    {
        builder.Services.AddSingleton<ImageOptimizationGate>();
        builder.Services.AddSingleton<PaddleTextDetector>();
        builder.Services.AddScoped<IDocumentOptimizationAdapter, ImageOptimizationAdapter>();
        builder.AddWorkflowActivity<PrepareOptimization>();
        builder.AddWorkflowActivity<AcceptOptimization>();
        builder.AddWorkflowActivity<DiscardOptimization>();
        builder.AddApprovalReviewProvider<OptimizationReview>();
        builder.AddWorkflow(Workflow);
    }

    public static BuiltInWorkflow Workflow { get; } = new(Id + ".optimize", "Optimize document storage",
        "Proposes a smaller image and asks workspace managers to review it before replacing the original.",
        JsonNode.Parse("""
        {
          "triggers": [
            { "type": "document.added", "list": "{param:list}", "data": { "source": "upload" } },
            { "type": "document.added", "list": "{param:list}", "data": { "source": "import" } },
            { "type": "document.added", "list": "{param:list}", "data": { "source": "restore" } },
            { "type": "manual", "list": "{param:list}" }
          ],
          "concurrency": "replace",
          "flow": {
            "start": "prepare",
            "nodes": {
              "prepare": { "activity": "paperdotnet.storageoptimization.prepare", "inputs": {
                "targetHeight": "{param:targetHeight}", "minConfidence": "{param:minConfidence}", "percentile": "{param:percentile}",
                "maxAnalysisDimension": "{param:maxAnalysisDimension}", "minimumScale": "{param:minimumScale}", "quality": "{param:quality}", "approvers": "{param:approvers}"
              }, "next": { "candidate": "review" } },
              "review": { "activity": "approval", "inputs": {
                "title": "Review smaller file for {title}", "assignees": "{step:prepare.approvers}",
                "review": { "type": "paperdotnet.storageoptimization.image", "key": "{step:prepare.candidate}" }
              }, "next": { "approved": "accept", "rejected": "discard" } },
              "accept": { "activity": "paperdotnet.storageoptimization.accept", "inputs": { "candidate": "{step:prepare.candidate}" } },
              "discard": { "activity": "paperdotnet.storageoptimization.discard", "inputs": { "candidate": "{step:prepare.candidate}" } }
            }
          }
        }
        """)!.AsObject())
    {
        Scope = BuiltInScope.Library,
        AllowManualLaunch = true,
        Parameters = JsonNode.Parse("""
        { "type": "object", "properties": {
          "targetHeight": { "type": "number", "default": 12, "description": "Smallest text target in pixels (4–200)." },
          "minConfidence": { "type": "number", "default": 50, "description": "Minimum DBNet box score or Tesseract word confidence (0–100)." },
          "percentile": { "type": "number", "default": 5, "description": "Height percentile (0–100)." },
          "maxAnalysisDimension": { "type": "number", "default": 2600, "description": "Maximum OCR image dimension (256–10000)." },
          "minimumScale": { "type": "number", "default": 0.05, "description": "Minimum resize scale (greater than 0, at most 1)." },
          "quality": { "type": "number", "default": 80, "description": "WebP quality (1–100)." },
          "approvers": { "type": "array", "description": "User names or group:Name; defaults to workspace managers." }
        } }
        """)!.AsObject(),
    };
}
