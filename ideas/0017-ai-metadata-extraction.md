# 0017: AI extraction of metadata into custom fields

- **Status:** mapped
- **Area:** AI / Documents
- **Date:** 2026-09-24
- **Mapped to:** [features.md](../docs/features.md): AI-01…04, AI-06, TSK-06

## The idea

Use AI to understand documents after OCR:

- **Classify:** suggest the content/document type (Invoice, Contract,
  Receipt…) and tags from the taxonomy (idea 0008).
- **Extract fields:** fill the content type's custom fields from the text,
  e.g. invoice number, amount, currency, due date, counterparty, IBAN.
  Uses the field schema as a structured-output schema.
- **Summarize:** a short summary and key dates. Can create tasks or events
  from them (e.g. "pay by 15 Oct" → a task, "appointment on 3 Nov" → an event).

Results are **suggestions with confidence**. The user or a rule accepts them;
high-confidence values can be applied automatically.

## Why / problem it solves

- Manual metadata entry is the biggest effort in a DMS.
- Structured fields make search, views, automation and reporting useful.
- Connects documents to tasks and calendar automatically.

## Examples / references

- `Microsoft.Extensions.AI` (`IChatClient` with structured output) and
  JSON Schema from field types (`JsonSchemaExporter`), see
  [dotnet-building-blocks.md](../docs/dotnet-building-blocks.md).
- Paperless-ngx / paperless-gpt style auto-classification.
- SharePoint Premium (Syntex) autofill columns and document processing models.

## Notes

<!-- Open questions to settle during review:
     - Self-hosting: off by default; works with a local model (Ollama, ONNX)
       or a cloud provider configured per tenant. Nothing must require AI.
     - Runs as a stage in the file-processing pipeline after OCR, or on
       demand; also callable from automation (idea 0009) and MCP (idea 0004).
     - Per content type prompts/instructions and few-shot examples; learning
       from user corrections (store accepted/rejected suggestions).
     - Validation: extracted values go through the same field-type validation
       and before-event handlers (idea 0012).
     - Privacy: per tenant choice of provider; option to never send document
       text to cloud models; audit what was sent.
     - Cost control: quotas and rate limits per tenant; cache by content hash
       (idea 0014).
     - Extension point: extensions provide specialized extractors (e.g. a
       dedicated invoice/ZUGFeRD parser without AI). -->
