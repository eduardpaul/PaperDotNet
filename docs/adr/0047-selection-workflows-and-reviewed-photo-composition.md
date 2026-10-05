# ADR-0047: Selection workflows and reviewed photo composition

Status: Accepted

Manual launches previously accepted many items but always started one run for
each. Creating a document from photographs requires one ordered selection, one
candidate, and one review before replacing any source.

## Decision

Manual triggers opt into `selectionMode: "selection"`; `perItem` is the existing
default. Ordered membership is persisted independently of a run's singular
primary item. Existing actions keep their singular context while activities,
tokens and scripts can access all targets. Concurrency checks compare overlapping
membership, including during interpreter recovery. Item deletion/purge cancels
selection runs, except the run causing its own reviewed recycling. Selection
locations remain immutable across moves.

The compiled `PaperDotNet.StorageOptimization` extension registers a manual
library workflow, Photo to document, with
`skip` concurrency. Its prepare activity snapshots the files and item versions,
normalizes static images, runs the configured OCR engine and stages a searchable
PDF. Temporary storage is separate from smaller-file storage optimization: a multipage PDF
need not be smaller than an individual source, and composition preserves the
primary image as file history. Staging identities come from workflow execution
identities. Retries reuse the original selection and candidate.

Core retains only reusable selection execution, transaction-aware recycling,
temporary document storage, conditional publishing and public processing contracts. The extension owns its
workflow definition, activities, source snapshots, freshness checks, review state,
image-format policy, normalization, one-page-per-photo policy, approver resolution
and review provider. These records live in its own `PhotoConversionDbContext`,
with provider-specific companion migration projects. `IStagedDocumentStore` has
no run, selection, primary item or approval fields. `IDocumentPublisher` preserves
file history in a caller-supplied transaction; the extension chooses the target
and whether to recycle anything. It references only the
extension SDK and calls `IOcrService` to use the existing configured OCR engine;
`IDocumentPdfRenderer` renders staged PDFs. Extension registrations use the
normal tenant enablement gates. No photo-specific services are registered by the
Documents module.

Launch presentation is declared with `x-paperdotnet-selection` in the input
schema. Generic selection controls interpret preview and label hints; they do
not recognize workflow keys. Review providers choose the shared `pdfComposition`
renderer and supply descriptions and decision labels. Developers can add other
selection workflows using these contracts without modifying the core modules.

A normal approval node resolves a composition review provider. Review checks
assignment, all source access, source freshness, and the launcher's continuing
write access. Commit repeats those checks and conditionally locks the reviewed
file and item versions. The primary PDF promotion, other-item recycling, audit
records and outbox rows share one transaction. The normal Wolverine outbox flush
commits an existing transaction, so reviewed batch writes use a separate save
path that defers commit and message dispatch. Live updates and retries of
announcements happen after commit.

Pending/preparing compositions protect their source and candidate blob
references. Cleanup abandons candidates of inactive runs after the normal grace
period. The generic store consults an extension-owned retention provider. Rejected candidates never change the
sources. SQLite and PostgreSQL migrations cover both membership and composition
temporary storage and extension state; API additions are regenerated into all SDKs.

## Consequences

Page order and the retained item's identity are independent choices. The
primary's metadata is retained; metadata from other source items is not merged.
The other source items remain recoverable through the recycle bin. Changed
selections require a fresh launch instead of silently regenerating an already
reviewed PDF. OCR is required before review, so missing or failing OCR prevents
this workflow from reaching approval. Decode/OCR work is serialized and bounded
per image; no image enhancement or cross-library composition is included.
