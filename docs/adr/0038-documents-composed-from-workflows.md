# ADR-0038: Documents are composed from workflows

- **Status:** Accepted (extends [ADR-0036](0036-workflows-as-the-core.md), changes its line on `DocumentProcessor`)
- **Date:** 2026-09-30

## Context

An upload ran one fixed pipeline, `DocumentProcessor`:
1. read the text layer of PDFs;
2. run OCR when a file had too little text, and store the result as a new version;
3. save page texts for search and render the thumbnail;
4. raise `document.processed`.

Libraries could only switch the whole pipeline off (`AutoProcess`) or OCR off (`OcrMode`).

The receipts package showed the problem. Its receipts are read by a model that sees the page images, so OCR is wasted work. The text and the thumbnail may not be wanted either. The project is meant to be composable: each of these steps should be something a library can remove or change, and other work should be able to follow it. ADR-0036 had kept processing as code; this ADR moves it into workflows.

## Decision

### Upload only stores the file

- An upload (a new document, or a new version of its file) stores the file and raises the trigger **`document.added`**. Its data: `version`, `mediaType`, `fileName`, `newDocument`.
- There is no processing on upload, no `document.processed`, and no processing status on file versions.
- The library settings `AutoProcess` and `OcrMode` are gone. `OcrLanguages` stays: it is the default of the OCR step.

### Each step is a built-in workflow, per library

Documents ships four built-in workflows (EVT-12). Each has a manual trigger too, to run it again on a document.

| Key | Name | Trigger | Default | What it does |
|---|---|---|---|---|
| `documents.text` | Read the text | `document.added` | on | `document.readText`: the PDF text layer as page texts, the page count, search. Raises `wf.documents.text.hasText` or `wf.documents.text.noText`. |
| `documents.thumbnail` | Make thumbnails | `document.added` | on | `document.thumbnail`: the thumbnail of the first page. |
| `documents.pages` | Render pages | `document.added` | on | `document.renderPages`: every page at the preview and large widths. |
| `documents.ocr` | Recognize text | `wf.documents.text.noText` | off | `document.ocr`: OCR as a new PDF version with page texts. |

- **Only what the workflows made is served.** Without "Render pages" a library has no page previews; without "Make thumbnails" it has no thumbnails; without "Read the text" (and OCR) its files have no text in search.
- **AI renders its own images.** `includeImages` renders the pages for the step and does not store them, so a library without previews can still send pages to a model.
- **OCR runs as an operation.** `document.ocr` starts the operation and waits for it with a bookmark (run again when it ends), so the run holds no server during OCR.
- **An OCR result is a new version like any other.** The operation raises `document.added` for it, so the library's workflows make its thumbnail and pages and read its text. That text raises `hasText`, which ends the chain.
- **The AI built-ins follow the text:** "Classify new documents" and "Extract fields" start on `wf.documents.text.hasText`.

### Built-in workflows can be per list

- A built-in workflow can have the scope *library*. It is then enabled per library: its row in the workspace belongs to a list (the list's id), and its triggers apply to that list only.
- The library's settings show its document workflows with a switch each.
- Built-ins that are on by default are created in a library the first time the library needs them: when a document is added, or when its settings are shown. Turning one off keeps its row, off.
- Templates write them with the library: `<w:Workflow BuiltIn="documents.ocr" List="Receipts" Enabled="false" />`.

### Workflows raise events other workflows follow

- Every workflow has a **stable `key`** (unique in the workspace; a built-in's is its built-in key). It is made from the name when the workflow is created, and a rename does not change it.
- When a run ends, the workflow raises **`wf.<key>.completed`** or **`wf.<key>.failed`**. The data: `runId`, `status`, and `error` when it failed.
- **`event.raise`** (flow activity: `event`, `data`) raises **`wf.<key>.<event>`**, e.g. `wf.documents.text.noText`.
- The events carry the run's item. They are triggers like any other (`"trigger": { "type": "wf.documents.text.noText" }`), with `list`, `terms` and `data` filters.
- A chain of workflows counts towards the loop protection: each hop adds one to the event's depth. The limit is 5.

## Consequences

- A fresh install behaves as before for text, thumbnails and previews. OCR is off until a library turns it on (packages can turn it on in their template).
- The document's side panel shows its workflow runs (status, errors, "Run again") instead of a processing status.
- Work that follows a step is a workflow on its event, not code in the pipeline: the same extension points as everything else.
- Migrations drop the processing columns of file versions and the two library settings. Existing libraries get the default workflows the first time they need them.
