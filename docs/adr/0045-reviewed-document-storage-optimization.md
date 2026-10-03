# ADR-0045: Reviewed document storage optimization

Status: Accepted

An oversized receipt photograph should become a smaller stored file only after
somebody reviews the actual result. This feature exercises the public extension
SDK rather than adding work to the upload endpoint.

## Decision

The compiled extension `paperdotnet.storageoptimization` ships in the host and is
optional per tenant. Its library workflow stages a candidate, waits on the normal
approval node, and accepts or discards it. The receipts template composes these
same activities before its AI reading nodes. `item.hasTerms` checks the current
receipt tags after review, including descendant terms.

`Documents.Contracts` exposes immutable version reading and staged replacement.
Documents owns hashing, storage references, conditional promotion, and cleanup.
Candidate rows own file references and metrics, not workflow execution state.
Workflow execution keys identify candidates so a retry reuses the same bytes.
Skipped analyses also retain a selection record, without additional file bytes,
so later tag changes reuse the original choice instead of running OCR again.
The database enforces one current file per tenant/item. Promotion claims the
reviewed current version inside a transaction; a stale candidate cannot replace
a newer upload. It releases only that source version, never unrelated history or
other documents with identical content. Events and activity entries can be
announced again after a committed promotion without duplication.

Approval nodes may contain `review: { type, key }`. Providers register with
`IExtensionBuilder.AddApprovalReviewProvider<T>()`; the runtime gates them per
tenant. Review endpoints check assignment and item access before resolving a
provider. Decisions repeat these checks and the provider's freshness check.
Ordinary approvals retain their existing behavior. The web app has a build-time
renderer registry; the first renderer compares authenticated original bytes and
candidate bytes at native 100% (one image pixel per CSS pixel). Unknown renderers
cannot enable decision buttons.

## Image adapter

`IDocumentOptimizationAdapter` separates format processing from storage and
approval. V1 handles static JPEG, PNG and WebP. Other formats and animated images
retain their original file; future PDF/Word adapters can use the same contract.
Animated PNG is detected through its animation-control chunk because Skia's PNG
codec exposes only its default frame. Animation is skipped before OCR.

SkiaSharp applies all EXIF orientations and bicubic resampling. Tesseract TSV
supplies word boxes. Analysis is capped at 2600 pixels on the longest dimension;
heights are projected to the upright source. Confidence, alphanumeric length,
and height filters precede an interpolated fifth percentile. Defaults are a
12-pixel target, 50% confidence, 0.05 minimum scale, WebP quality 80, and no
upscaling. No reliable text or no byte savings means no approval is requested.
Decoder/OCR failures fail the workflow while keeping the source.

One processor per host process and a default 64-million-pixel decode limit bound
optimization work. `StorageOptimization:MaxPixels` and
`StorageOptimization:OcrTimeoutSeconds` configure resource limits. The existing
`Documents:TesseractPath` configures the CLI. Pending reviews have no deadline.
They protect their source and candidate from cleanup; finished/cancelled runs
release abandoned candidates. Purging an item releases its pending candidates.
Retrying a failed prepare after cleanup regenerates the candidate under its
original execution key, while still requiring the same immutable source.

## Consequences

Accepting optimization is an explicit exception to retaining every original
file version. Source bytes are unavailable for restoration after reclamation;
metrics and decisions remain in the audit/activity history. Savings shown before
approval are prospective: shared content and other retained versions can prevent
physical reclamation. Cleanup retains its one-hour grace period.

The adjacent POC's word strategy accidentally fell through to row analysis.
Production implements word analysis independently and records fresh benchmarks.
Word measurements, OCR prescaling, and compression do not prove preservation of
all text. Manager review remains mandatory; synthetic and real-image benchmark
results include a second OCR pass as diagnostic evidence.

The planned mixed-case extension ID was normalized to lowercase to satisfy the
existing manifest contract. Both database providers have migrations, and the
HTTP additions are included in all generated SDKs.

Receipt tag triggers use an optional per-trigger `concurrency: "skip"` override.
Its effective value is saved with the run, so recovery applies the same policy.
Tag changes retain the active review and the later tag check observes the current
item. Upload triggers retain `replace`, cancelling reviews of obsolete uploads.
Concurrent scheduler initialization tolerates a competing insertion of the same
trigger state; durable occurrence IDs continue to prevent duplicate starts.
