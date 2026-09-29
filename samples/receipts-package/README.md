# Receipts: a sample package

A ready-made workspace that reads receipts ("tickets") with AI in the batch
window. You upload a photo or a PDF of a receipt and tag it **ticket** (or a tag
below it: Groceries, Restaurant, Fuel). The next AI batch reads the following
into the receipt's fields:
- the store;
- the date;
- the currency;
- the total.

It also creates one item per line (description, quantity, unit price, amount)
in the list **Receipt lines**.

It shows the two ways PaperDotNet is meant to be changed:

- **Portable configuration.** Everything in the workspace is one template
  ([template.xml](template.xml)): the tags, the content types, the library,
  the lines list with its lookup to the receipt, the views, the OCR languages,
  and the workflows. The AI part is part of the configuration too: the prompt,
  the JSON schema of the answer, batch execution and images. You can apply it
  to any organization, export it, and change it without writing code.
- **Extensions for what configuration cannot do.** Turning the AI's JSON into
  typed fields and line items is one workflow action, `samples.receipts.save`,
  in the sample extension
  [PaperDotNet.Samples.Receipts](../PaperDotNet.Samples.Receipts/ReceiptsExtension.cs).
  The extension references only the extension SDK and adds the action with the
  same call the product's own modules use (`AddWorkflowActivity`).

## What the template contains

| Part | What it is |
|---|---|
| Extension `samples.receipts` | Enabled for the organization |
| Term set `Receipts/Tags` | **ticket** (synonym *receipt*) with Groceries, Restaurant and Fuel below it; Warranty |
| Content type **Receipt** | `tags`, `status` (New, Read, Needs review), `store`, `purchaseDate`, `currency`, `total` |
| Content type **Receipt line** | `receipt` (lookup to the library), `quantity`, `unitPrice`, `amount`; the title is the description |
| Workspace **Receipts** (parameter `Workspace`) | The library **Receipts** (views: All receipts, Needs review) and the list **Receipt lines** |
| Workflow **Read tagged receipts** | When a receipt's tags change to one at or below *ticket* |
| Workflow **Read new receipts** | When a file uploaded with the tag has been processed (its text exists) |
| Built-in workflow **AI batch** | Sends the waiting questions on the schedule `BatchSchedule` (default: every hour) |

Both reading workflows run the same flow:

1. `read` (`ai.prompt`): asks the model for the receipt as JSON, following the
   template's schema. It sends:
   - the OCR text of the file;
   - the first two pages as images (`includeImages`). Photos of receipts need
     the images: OCR often loses the prices in the right-hand column.

   With `execution: batch` the step waits for the AI batch (the run holds no
   server while it waits). With `onDeadline: fail` it never falls back to an
   immediate call.
2. `save` (`samples.receipts.save`): writes the fields, sets the status to
   *Read*, and replaces the lines of an earlier reading.
3. On an error in either step, `review` (`item.update`) sets the status to
   *Needs review*.

## Try it

1. **Build a server with the sample extension:**

   ```bash
   dotnet run --project src/PaperDotNet.Host -p:IncludeSamples=true
   ```

   `IncludeSamples` adds the sample extensions to the host build. It is off in
   normal builds and images; a real deployment references its extensions as
   packages (see [extensions.md](../../docs/extensions.md#7-add-it-to-a-host-build)).

2. **Configure a model that reads images, ideally with a batch API.** For
   example, Azure AI Foundry with a *Global Batch* deployment of gpt-4.1:

   ```bash
   PAPERDOTNET__AI__Chat__Provider=openai
   PAPERDOTNET__AI__Chat__Endpoint=https://<resource>.services.ai.azure.com/openai/v1/
   PAPERDOTNET__AI__Chat__Model=gpt-4.1
   PAPERDOTNET__AI__Chat__ApiKey=<key>
   PAPERDOTNET__AI__Batch__Provider=openai
   ```

   Keep the key in an environment variable or a secret store, never in a
   template. Without `AI:Batch:Provider`, the AI batch asks the chat model
   directly, one question at a time, at the batch time.

3. **Apply the template** (as an administrator, with `template.manage`):

   ```bash
   curl -X POST "$URL/v1.0/provisioning/apply?parameters%5BBatchSchedule%5D=*/10%20*%20*%20*%20*" \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/xml" \
     --data-binary @samples/receipts-package/template.xml
   ```

   Add `dryRun=true` first to see what it would change. Parameters:
   - `Workspace` (Receipts);
   - `Tag` (ticket);
   - `BatchSchedule` (a cron expression in UTC; default every hour);
   - `OcrLanguages` (eng; for example deu+eng).

   Applying it again changes nothing.

4. **Upload a photo or a PDF** to the library *Receipts* and tag it *ticket*.
   The run waits for the next batch window. Azure's batches usually finish
   within minutes (at most 24 hours). Then the receipt shows the store, date
   and total, and *Receipt lines* has one item per line.

## Tests

- [ReceiptsPackageTests](../../tests/PaperDotNet.IntegrationTests/ReceiptsPackageTests.cs)
  applies this template as it ships, against a fake batch API. It then:
  - tags an uploaded receipt;
  - runs the batch window;
  - checks the fields, the lines and the images sent;
  - tags the receipt again, and checks that the lines are replaced.
- [OpenAiBatchClientTests](../../tests/PaperDotNet.UnitTests/OpenAiBatchClientTests.cs)
  checks the requests the batch client sends and how it reads the answers.
