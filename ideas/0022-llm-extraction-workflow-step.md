# 0022: LLM extraction workflow step, attached to document types/tags

- **Status:** mapped
- **Area:** AI / Automation / Documents
- **Date:** 2026-09-27
- **Mapped to:** [features.md](../docs/features.md): AI-07 (also touches AI-01…03, EVT-07…09, LST-02)

## The idea

An automation action ("workflow step") that runs an LLM extraction with
structured output and maps the result onto the item's fields. It is attached
to a content type or tag, so it triggers automatically for matching documents
(e.g. "when tagged *Supermarket receipt*, extract fields with this step").

The step is configured, not coded:

1. Pick which fields of the content type to fill (a subset of the content
   type's schema, idea 0017 already does this for JSON Schema generation).
2. Optionally add a prompt/instructions and a few example documents (few-shot).
3. The LLM call uses structured output constrained to the generated schema;
   the response is mapped 1:1 onto those fields as a suggestion (with
   confidence) or applied directly, depending on automation settings.

**Sample:** a "Supermarket receipt" content type with fields `store` (text),
`purchaseDate` (date), `total` (money), and a `products` field (repeatable
group: name, quantity, unit price). A rule "when a document is tagged
*Receipt*" runs the extraction step, which reads the OCR text and fills all
four fields from one LLM call.

## Why / problem it solves

- Idea 0017 describes AI extraction as a pipeline capability; this idea makes
  it a reusable **automation step** so it can be composed with other actions
  (move, tag, notify) and scoped to specific content types/tags instead of
  running for everything.
- Turns unstructured receipts/invoices/statements into queryable, reportable
  list data without per-document-type custom code.

## Examples / references

- Idea [0017](0017-ai-metadata-extraction.md) (AI metadata extraction) and
  idea [0009](0009-automation-rules-engine-elsa.md) (automation engine,
  `IAutomationAction`, per ADR-0019/ADR-0024).
- `Microsoft.Extensions.AI` structured output + `JsonSchemaExporter` from the
  content type's field schema (as already noted in idea 0017).
- Paperless-gpt / paperless-ngx workflows with an AI extraction stage.

## Notes

<!-- Open questions to settle during review:
     - Is this a new `IAutomationAction` (e.g. `ExtractFieldsWithAi`) that
       reuses the AI-03 extraction engine, rather than a separate feature?
     - Repeatable/table fields (like `products` on a receipt) need a field
       type that isn't in the current LST-03 list (core field types) —
       does this need a new "list/table" field type, or is it out of scope
       for v1 (only scalar fields)?
     - Same suggestion/confidence and acceptance flow as AI-03: does the
       automation apply high-confidence values directly, or always create a
       suggestion for the user to accept?
     - Respects AI-01 (per-tenant provider config) and AI-06 (quotas,
       caching by content hash, audit); the step should not run twice on an
       unchanged document. -->
