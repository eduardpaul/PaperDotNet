using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaperDotNet.Extensions;
using PaperDotNet.Lists.Contracts;
using PaperDotNet.Samples.Receipts;
using PaperDotNet.Workflows.Contracts;

[assembly: PaperDotNetExtension(typeof(ReceiptsExtension))]

namespace PaperDotNet.Samples.Receipts;

/// <summary>
/// The code part of the receipts sample. Everything else (the library, the lines list, the "ticket" tag, the workflow that
/// asks the AI in the batch window) is configuration in the package <c>samples/receipts-package</c>.
/// </summary>
public sealed class ReceiptsExtension : IExtension
{
    public const string Id = "samples.receipts";

    public void Configure(IExtensionBuilder builder) => builder.AddWorkflowActivity<SaveReceiptAction>();
}

/// <summary>
/// Action <c>samples.receipts.save</c>: writes what the AI read from a receipt (<c>receipt</c>: JSON text, usually
/// <c>{step:read.json}</c>) to the receipt's fields (<c>store</c>, <c>purchaseDate</c>, <c>currency</c>, <c>total</c>,
/// <c>status</c>) and one item per line to the lines list (<c>linesList</c>, default <c>Receipt lines</c>) with a lookup
/// to the receipt (<c>lookupField</c>, default <c>receipt</c>). Lines of an earlier reading of the same receipt are
/// removed. Safe to repeat: line ids are derived from the execution id.
/// </summary>
public sealed class SaveReceiptAction(IListItemStore items) : IWorkflowActivity
{
    public string Key => $"{ReceiptsExtension.Id}.save";

    public string Description => "Saves a receipt read by AI: { \"receipt\": \"{step:read.json}\", \"linesList\": \"Receipt lines\" }.";

    public IEnumerable<string> Validate(JsonObject inputs) => ActivityInputs.Required(inputs, "receipt");

    public JsonObject? InputSchema => ActivitySchemas.Of(["receipt"],
        ("receipt", ActivitySchemas.Text("The receipt as JSON (store, date, currency, total, lines: description, quantity, unitPrice, amount), e.g. {step:read.json}.")),
        ("linesList", ActivitySchemas.Text("The list for the lines, by name (default: Receipt lines).")),
        ("lookupField", ActivitySchemas.Text("The lines' lookup field to the receipt (default: receipt).")));

    public JsonObject? OutputSchema => ActivitySchemas.Of([],
        ("store", ActivitySchemas.Text("The store.")),
        ("total", ActivitySchemas.Number("The total.")),
        ("lines", ActivitySchemas.Number("Lines saved.")));

    public async Task<WorkflowActivityResult> ExecuteAsync(WorkflowActivityContext context, CancellationToken cancellationToken)
    {
        if (context.Item is not { } item)
        {
            return WorkflowActivityResult.Fail("A receipt is required.");
        }

        JsonObject receipt;
        try
        {
            receipt = JsonNode.Parse(await context.ExpandAsync(ActivityInputs.Text(context.Inputs, "receipt")!, cancellationToken)) as JsonObject
                ?? throw new JsonException("Not an object.");
        }
        catch (JsonException)
        {
            return WorkflowActivityResult.Fail("receipt must be a JSON object, e.g. {step:read.json}.");
        }

        var store = items.AsSystem();
        var linesName = ActivityInputs.Text(context.Inputs, "linesList") ?? "Receipt lines";
        var lookup = ActivityInputs.Text(context.Inputs, "lookupField") ?? "receipt";
        var linesList = (await store.GetListsAsync(context.WorkspaceId, null, cancellationToken)).FirstOrDefault(l => l.Name == linesName);
        if (linesList is null)
        {
            return WorkflowActivityResult.Fail($"The list '{linesName}' does not exist in the workspace.");
        }

        // One item per line, with ids derived from this execution: a repeat finds the lines it created.
        var lines = (receipt["lines"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var saved = new HashSet<Guid>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var fields = new JsonObject
            {
                ["title"] = Text(line, "description") is { } description ? Truncate(description, 255) : $"Line {index + 1}",
                [lookup] = item.ItemId.ToString(),
            };
            foreach (var name in new[] { "quantity", "unitPrice", "amount" })
            {
                if (Number(line, name) is { } value)
                {
                    fields[name] = value;
                }
            }

            var created = await store.CreateAsync(context.WorkspaceId, linesList.Id, LineId(context.ExecutionId, index), fields, null, cancellationToken);
            if (!created.Succeeded)
            {
                return WorkflowActivityResult.Fail($"Line {index + 1}: {created.Describe()}");
            }

            saved.Add(created.Item!.Id);
        }

        // Lines of an earlier reading of this receipt (e.g. it was tagged again) are replaced.
        var (earlier, error) = await store.QueryAsync(context.WorkspaceId, linesList.Id,
            new ListItemQuery($"fields/{lookup} eq {item.ItemId}", Top: ListItemQuery.MaxTop), cancellationToken);
        if (error is not null)
        {
            return WorkflowActivityResult.Fail(error);
        }

        foreach (var old in earlier.Where(l => !saved.Contains(l.Id)))
        {
            await store.DeleteAsync(context.WorkspaceId, linesList.Id, old.Id, null, cancellationToken);
        }

        var total = Number(receipt, "total") ?? lines.Sum(l => Number(l, "amount") ?? 0);
        var header = new JsonObject { ["total"] = total, ["status"] = "Read" };
        if (Text(receipt, "store") is { } storeName)
        {
            header["store"] = Truncate(storeName, 255);
        }

        if (Text(receipt, "date") is { } date && DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var day))
        {
            header["purchaseDate"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (Text(receipt, "currency") is { Length: 3 } currency)
        {
            header["currency"] = currency.ToUpperInvariant();
        }

        var updated = await store.UpdateAsync(item.WorkspaceId, item.ListId, item.ItemId, header, null, cancellationToken);
        return updated.Succeeded
            ? WorkflowActivityResult.Ok(new JsonObject { ["store"] = header["store"]?.DeepClone(), ["total"] = total, ["lines"] = lines.Count })
            : WorkflowActivityResult.Fail(updated.Describe());
    }

    /// <summary>The id of a line: derived from the execution and the line's position (a name-based UUID, version 8).</summary>
    public static Guid LineId(Guid executionId, int index)
    {
        Span<byte> input = stackalloc byte[20];
        executionId.TryWriteBytes(input);
        BitConverter.TryWriteBytes(input[16..], index);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A number, also when the model wrote it as text (<c>"2,99"</c> or <c>"2.99 €"</c>).</summary>
    private static decimal? Number(JsonObject node, string name)
    {
        if (node[name] is not JsonValue value)
        {
            return null;
        }

        if (value.GetValueKind() == JsonValueKind.Number)
        {
            return value.GetValue<decimal>();
        }

        var text = value.GetValueKind() == JsonValueKind.String
            ? new string([.. value.GetValue<string>().Where(c => char.IsAsciiDigit(c) || c is '.' or ',' or '-')]).Replace(',', '.')
            : null;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
