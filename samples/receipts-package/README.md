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

**It is configuration only: no code.** Everything is one template
([template.xml](template.xml)), which you can apply to any organization,
export, and change:
- the tags, the content types and the views;
- the library and lines list, connected through directed global relationships;
- the OCR languages;
- the workflows.

The workflows cover the AI part too: the prompt, the JSON schema of the answer,
batch execution and images. A small JavaScript script node,
inside the workflow's JSON, saves the answer into the lists. It runs in the
server's sandbox, so the package needs no extension.

The library shows how documents are composed from workflows
([ADR-0038](../../docs/adr/0038-documents-composed-from-workflows.md)): an
upload only stores the file, and the library's own workflows decide the rest.
Here it keeps thumbnails and page images, but turns off reading the text and
leaves OCR off, because the model reads the receipt from its images.

## What the template contains

| Part | What it is |
|---|---|
| Term set `Receipts/Tags` | **ticket** (synonym *receipt*) with Groceries, Restaurant and Fuel below it; Warranty |
| Content type **Receipt** | `tags`, `status` (New, Read, Needs review), `store`, `purchaseDate`, `currency`, `total` |
| Content type **Receipt line** | `quantity`, `unitPrice`, `amount`; the title is the description |
| Relationship type **contains receipt line** | Directed; inverse **belongs to receipt**; at most one receipt per line |
| Workspace **Receipts** (parameter `Workspace`) | The library **Receipts** (views: All receipts, Needs review) and the list **Receipt lines** |
| Workflow **Read receipts** | Two triggers: when a receipt's tags change to one at or below *ticket*, and when a file with the tag is added |
| Library workflows | "Read the text" off (as is "Recognize text"); "Make thumbnails" and "Render pages" on |
| Built-in workflow **AI batch** | Sends the waiting questions on the schedule `BatchSchedule` (default: every hour) |

The reading workflow has two triggers, because a receipt comes in two ways:
tagged after the upload (the tags change), or added with the tag
(`document.added`, e.g. from an import). Either one starts the same flow:

1. `read` (`ai.prompt`): asks the model for the receipt as JSON, following the
   template's schema. It sends the first two pages as images
   (`includeImages`), and no text: the model reads the receipt from the image,
   where OCR would often lose the prices in the right-hand column. The images
   are rendered for the step, so they do not depend on the library's page
   images.

   With `execution: batch` the step waits for the AI batch (the run holds no
   server while it waits). With `onDeadline: fail` it never falls back to an
   immediate call.
2. `save` (`script`): reads the answer from `steps.read.json`, then:
   - writes the store, date, currency and total, and sets the status to *Read*;
   - removes the lines of an earlier reading (the receipt can be read again
     by tagging it again);
   - creates one item in *Receipt lines* per line and links it with
     **contains receipt line** (inverse: **belongs to receipt**).

   The script pages through `items.related`, then plans `items.unrelate`,
   `items.deleteById`, and `items.relate` writes. Membership follows item ids,
   so moving a receipt or a line to a compatible list does not break the link.
   Quantity and price belong to a purchase line, rather than a reusable product.

   The writes are applied after the script, safely: a retry never creates a
   line twice.
3. On an error in any step, `review` (`item.update`) sets the status to
   *Needs review*.

With `"concurrency": "replace"`, tagging a receipt again while it waits for
the batch replaces the waiting run. The script can be tried outside the
workflow with `runWorkflowScript` from the TypeScript SDK.

## Try it

1. **Run any PaperDotNet server** (`dotnet run --project src/PaperDotNet.Host`,
   or the container). It needs nothing extra.

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
- The workflow script contract tests
  ([scripts.test.mjs](../../sdk/typescript/test/scripts.test.mjs)) run the
  same script API on the server and in the SDK.
- [OpenAiBatchClientTests](../../tests/PaperDotNet.UnitTests/OpenAiBatchClientTests.cs)
  checks the requests the batch client sends and how it reads the answers.

## Upgrading an older lookup-based installation

The package is additive: it does not remove existing columns or silently rewrite existing lines. If the earlier
package is installed, pause **Read receipts** before reapplying this version. Make the **Receipt line / receipt**
lookup optional in the content type editor; new graph-based lines do not populate that column. Reapply the package,
then backfill each existing line's receipt id as a **contains receipt line** relationship before resuming readings.
This preserves its membership so the next reading can remove the earlier generated lines.

With an authenticated TypeScript SDK client and the existing lines-list builder, the backfill is:

```ts
for await (const line of all(lines.items)) {
  const receipt = fieldsOf(line).receipt;
  if (typeof receipt === 'string') {
    await client.api.v10.items.byItemId(receipt).relationships.post({
      otherId: line.id,
      type: 'contains receipt line',
    });
  }
}
```

`all` and `fieldsOf` come from `@paperdotnet/client`. Repeating this backfill does not duplicate links. Keep the old
lookup column until any views, scripts or integrations that use it have been updated.
