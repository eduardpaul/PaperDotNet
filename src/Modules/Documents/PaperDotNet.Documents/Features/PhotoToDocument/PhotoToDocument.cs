using PaperDotNet.Documents.Features.StorageOptimization;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PaperDotNet.Abstractions;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.Ocr.Contracts;
using PaperDotNet.Identity.Contracts;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Workflows.Contracts;
using PaperDotNet.Workspaces.Contracts;
using SkiaSharp;

namespace PaperDotNet.Documents.Features.PhotoToDocument;

internal static class PhotoToDocument
{
    public const string Key = StorageOptimizationWorkflows.Id + ".photoToDocument";
    public const string ReviewType = StorageOptimizationWorkflows.Id + ".composition";
    public static readonly BuiltInWorkflow Workflow = new(Key, "Photo to document",
        "Combines selected photos into one searchable PDF for approval before replacing the primary photo and recycling the others.",
        JsonNode.Parse("""
        {
          "scope": "list",
          "inputSchema": { "type": "object", "properties": {}, "x-paperdotnet-selection": {
            "preview": "image", "itemLabel": "page", "orderLabel": "Page order",
            "primaryDescription": "Photo to document replaces this item with the approved PDF and recycles the other photos. Its metadata and original file history are retained."
          } },
          "trigger": { "type": "manual", "list": "{param:list}", "selectionMode": "selection" },
          "concurrency": "skip",
          "flow": { "start": "prepare", "nodes": {
            "prepare": { "activity": "paperdotnet.storageoptimization.composePhotos", "inputs": { "approvers": "{param:approvers}" }, "next": { "done": "review" } },
            "review": { "activity": "approval", "inputs": {
              "title": "Review combined PDF for {title}", "assignees": "{step:prepare.approvers}",
              "review": { "type": "paperdotnet.storageoptimization.composition", "key": "{step:prepare.candidate}" }
            }, "next": { "approved": "accept", "rejected": "discard" } },
            "accept": { "activity": "paperdotnet.storageoptimization.acceptComposition", "inputs": { "candidate": "{step:prepare.candidate}" } },
            "discard": { "activity": "paperdotnet.storageoptimization.discardComposition", "inputs": { "candidate": "{step:prepare.candidate}" } }
          } }
        }
        """)!.AsObject())
    {
        Scope = BuiltInScope.Library,
        AllowManualLaunch = true,
        Parameters = ActivitySchemas.Of([], ("approvers", ActivitySchemas.People("Reviewers; defaults to workspace managers."))),
    };
}

internal sealed class PhotoCompositionGate : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public void Dispose() => Semaphore.Dispose();
}

internal sealed class ComposePhotosActivity(PhotoCompositionGate gate, ITenantScopeFactory scopes, ITenantContext tenant,
    IWorkspaceAccess workspaces, IWorkflowRecipients recipients, IUserDirectory users) : IWorkflowActivity
{
    public string Key => "paperdotnet.storageoptimization.composePhotos";
    public string Description => "Stages a searchable PDF from the ordered selection of static JPEG, PNG or WebP photos.";
    public JsonObject? InputSchema => PhotoToDocument.Workflow.Parameters;
    public JsonObject? OutputSchema => ActivitySchemas.Of([], ("candidate", ActivitySchemas.Text("Staged PDF id.")), ("approvers", ActivitySchemas.People("Resolved reviewers.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } primary || context.UserId is not { } actor || context.RunId is not { } runId || context.Items.Count == 0)
            return WorkflowActivityResult.Fail("Select photos to convert in a manually launched workflow.");
        var people = context.Inputs["approvers"];
        if (people is JsonValue token && token.TryGetValue<string>(out var template)) people = await context.ResolveAsync(template, cancellationToken);
        var specs = ActivityInputs.Texts(new JsonObject { ["approvers"] = people?.DeepClone() }, "approvers");
        IReadOnlyList<Guid> assignees;
        if (specs is { Count: > 0 })
        {
            var resolved = await recipients.ResolveAsync(specs, context, cancellationToken);
            if (resolved.Unknown.Count > 0) return WorkflowActivityResult.Fail($"Unknown approvers: {string.Join(", ", resolved.Unknown)}.");
            assignees = resolved.Users;
        }
        else assignees = (await workspaces.GetMembersAsync(primary.WorkspaceId, cancellationToken)).Where(m => m.Level >= WorkspaceAccessLevel.Manage).Select(m => m.UserId).ToArray();
        var active = new List<Guid>();
        foreach (var id in assignees) if (await users.IsActiveAsync(id, cancellationToken)) active.Add(id);
        var names = await users.GetUserNamesAsync(active, cancellationToken);
        if (names.Count == 0) return WorkflowActivityResult.Fail("No active configured approvers or workspace managers can review the PDF.");

        await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!, actor);
        var store = scope.ServiceProvider.GetRequiredService<PhotoConversionStore>();
        var files = scope.ServiceProvider.GetRequiredService<IDocumentFileStore>();
        var items = scope.ServiceProvider.GetRequiredService<IListItemStore>();
        var candidate = await store.GetAsync(context.ExecutionId, cancellationToken);
        if (candidate is null)
        {
            var sources = new List<PhotoSource>();
            foreach (var target in context.Items)
            {
                var item = await items.GetAsync(target.WorkspaceId, target.ListId, target.ItemId, cancellationToken);
                var file = await files.GetCurrentAsync(target.ItemId, cancellationToken);
                if (item is null || item.Access < WorkspaceAccessLevel.Contribute || item.IsFolder || file is null
                    || file.WorkspaceId != primary.WorkspaceId || file.ListId != primary.ListId
                    || file.MediaType is not ("image/jpeg" or "image/png" or "image/webp"))
                    return WorkflowActivityResult.Fail("Every selected item must be a writable static JPEG, PNG or WebP photo in the same library.");
                sources.Add(new(file, item.Version));
            }
            var languages = sources.Single(s => s.File.ItemId == primary.ItemId).File.AnalysisLanguages ?? "eng";
            candidate = await store.BeginAsync(context.ExecutionId, runId, primary.ItemId, actor, sources, languages, cancellationToken);
        }
        if (candidate.State == "preparing") candidate = await store.BeginAsync(candidate.Id, runId, primary.ItemId, actor, candidate.Sources, candidate.Languages, cancellationToken);
        if (candidate.State is "pending" or "accepted") return Output(candidate.Id, names.Values);
        if (!await store.IsFreshAsync(candidate.Id, true, cancellationToken)) return WorkflowActivityResult.Fail("A source changed; launch a new conversion.");
        await gate.Semaphore.WaitAsync(cancellationToken);
        DirectoryInfo? work = null;
        try
        {
            work = Directory.CreateTempSubdirectory("pdn_compose_");
            var paths = new List<string>();
            for (var i = 0; i < candidate.Sources.Count; i++)
            {
                await using var content = await files.OpenVersionAsync(candidate.Sources[i].File.Id, cancellationToken)
                    ?? throw new InvalidOperationException("A source photo is unavailable.");
                var source = Path.Combine(work.FullName, $"source-{i}");
                await using (var output = File.Create(source)) await content.CopyToAsync(output, cancellationToken);
                var image = Path.Combine(work.FullName, $"page-{i}.png");
                Normalize(source, image);
                paths.Add(image);
            }
            var ocr = scope.ServiceProvider.GetRequiredService<IOcrService>();
            var pages = new List<Stream>();
            try
            {
                foreach (var path in paths) pages.Add(File.OpenRead(path));
                await using var result = await ocr.RecognizeAsync(pages, candidate.Languages, cancellationToken, allowEmpty: true);
                if (result.PageTexts.Count != candidate.Sources.Count) throw new InvalidOperationException("OCR must produce one page per photo.");
                candidate = await store.StageAsync(candidate.Id, result.Pdf, result.PageTexts, cancellationToken);
            }
            finally { foreach (var page in pages) await page.DisposeAsync(); }
            return Output(candidate.Id, names.Values);
        }
        finally { try { work?.Delete(recursive: true); } finally { gate.Semaphore.Release(); } }
    }

    private static WorkflowActivityResult Output(Guid id, IEnumerable<string> names) => WorkflowActivityResult.Ok(new JsonObject
    {
        ["candidate"] = id.ToString(), ["approvers"] = new JsonArray([.. names.Select(n => JsonValue.Create(n))]),
    });

    internal static void Normalize(string sourcePath, string destination)
    {
        using var source = File.OpenRead(sourcePath);
        using var codec = SKCodec.Create(source) ?? throw new InvalidOperationException("A photo cannot be decoded.");
        using var probe = File.OpenRead(sourcePath);
        if (codec.FrameCount > 1 || IsAnimatedPng(probe) || IsAnimatedWebP(probe)) throw new InvalidOperationException("Animated photos cannot be combined.");
        if ((long)codec.Info.Width * codec.Info.Height > 64_000_000) throw new InvalidOperationException("A photo exceeds the 64-million-pixel decode limit.");
        using var decoded = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) throw new InvalidOperationException("A photo is damaged.");
        var origin = codec.EncodedOrigin;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        using var upright = new SKBitmap(swap ? decoded.Height : decoded.Width, swap ? decoded.Width : decoded.Height);
        using var canvas = new SKCanvas(upright);
        var w = decoded.Width;
        var h = decoded.Height;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Flush();
        using var image = SKImage.FromBitmap(upright);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(destination);
        png.SaveTo(output);
    }

    private static bool IsAnimatedPng(Stream source)
    {
        Span<byte> header = stackalloc byte[8];
        if (source.Read(header) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        while (source.Position + 8 <= source.Length)
        {
            source.ReadExactly(header);
            if (header[4..].SequenceEqual("acTL"u8)) return true;
            if (header[4..].SequenceEqual("IDAT"u8) || header[4..].SequenceEqual("IEND"u8)) return false;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length > source.Length - source.Position - 4) return false;
            source.Seek((long)length + 4, SeekOrigin.Current);
        }
        return false;
    }

    private static bool IsAnimatedWebP(Stream source)
    {
        source.Position = 0;
        Span<byte> header = stackalloc byte[12];
        if (source.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8)) return false;
        while (source.Position + 8 <= source.Length)
        {
            source.ReadExactly(header[..8]);
            if (header[..4].SequenceEqual("ANIM"u8)) return true;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            if (length > source.Length - source.Position) return false;
            if (header[..4].SequenceEqual("VP8X"u8) && length > 0) return (source.ReadByte() & 2) != 0;
            source.Seek((long)length + (length & 1), SeekOrigin.Current);
        }
        return false;
    }
}

internal sealed class AcceptCompositionActivity(ITenantScopeFactory scopes, ITenantContext tenant) : IWorkflowActivity
{
    public string Key => "paperdotnet.storageoptimization.acceptComposition";
    public string Description => "Promotes the reviewed PDF and atomically recycles its other source photos.";
    public JsonObject? InputSchema => ActivitySchemas.Of(["candidate"], ("candidate", ActivitySchemas.Text("Staged PDF id.")));
    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "candidate");
    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.UserId is not { } actor) return WorkflowActivityResult.Fail("A conversion requires its launching user.");
        await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!, actor);
        var store = scope.ServiceProvider.GetRequiredService<PhotoConversionStore>();
        var id = Guid.Parse(await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "candidate")!, cancellationToken));
        if ((await store.GetAsync(id, cancellationToken))?.RunId != context.RunId) return WorkflowActivityResult.Fail("The candidate belongs to another run.");
        return await store.PromoteAsync(id, cancellationToken) ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail("A source changed or access was removed; launch a new conversion.");
    }
}

internal sealed class DiscardCompositionActivity(ITenantScopeFactory scopes, ITenantContext tenant) : IWorkflowActivity
{
    public string Key => "paperdotnet.storageoptimization.discardComposition";
    public string Description => "Discards the reviewed PDF and keeps every source photo.";
    public JsonObject? InputSchema => ActivitySchemas.Of(["candidate"], ("candidate", ActivitySchemas.Text("Staged PDF id.")));
    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "candidate");
    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.UserId is not { } actor) return WorkflowActivityResult.Fail("A conversion requires its launching user.");
        await using var scope = scopes.CreateScope(tenant.TenantId!.Value, tenant.TenantIdentifier!, actor);
        var store = scope.ServiceProvider.GetRequiredService<PhotoConversionStore>();
        var id = Guid.Parse(await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "candidate")!, cancellationToken));
        if ((await store.GetAsync(id, cancellationToken))?.RunId != context.RunId) return WorkflowActivityResult.Fail("The candidate belongs to another run.");
        return await store.DiscardAsync(id, cancellationToken) ? WorkflowActivityResult.Ok() : WorkflowActivityResult.Fail("The candidate cannot be discarded.");
    }
}
