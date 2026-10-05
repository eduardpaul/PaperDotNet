# ADR-0046: Reviewed document storage optimization

Status: Accepted

OCR implementation and model ownership now live in the shared OCR module; see
[ADR-0048](0048-shared-ocr-module.md). The extension consumes its public contracts.


An oversized receipt photograph should become a smaller stored file only after
somebody reviews the actual result. This feature exercises the public extension
SDK rather than adding work to the upload endpoint.

## Decision

The compiled extension `paperdotnet.storageoptimization` ships in the host and is
optional per tenant. Its library workflow stages a candidate, waits on the normal
approval node, and accepts or discards it. The receipts template composes these
same activities before its AI reading nodes. `item.hasTerms` checks the current
receipt tags after review, including descendant terms.

Once the extension is enabled, people with Contribute access can launch
"Optimize document storage" from a file's Preview tab or the library selection
toolbar, including files in Inbox. The library switch controls automatic runs
only. The workflow opts into `AllowManualLaunch`; a manual launch resolves the
library's configured parameters, creating an off workflow row with defaults
when none exists. It never changes the automatic setting. The library built-in
catalog exposes this policy and the launch form, and
`POST …/lists/{listId}/workflows/builtIns/{key}/runs` checks all selected items
before creating the workflow or starting runs. Disabling the extension also
blocks manual launches through existing workflow name/id endpoints.

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

SkiaSharp applies all EXIF orientations and bicubic resampling. Small-text
analysis defaults to PaddleOCR PP-OCRv6 small DBNet, running in-process on CPU
through RapidOcrNet and Microsoft.ML.OnnxRuntime. RapidOcrNet supplies detection
preprocessing, contour scoring, minimum-area boxes and polygon expansion using
SkiaSharp and Clipper2; OpenCV and additional services are unnecessary. The
Apache-2.0 ONNX detector is pinned by SHA-256 and shipped in build/publish output;
recognition/classification models are excluded. Inference needs no network.
ONNX Runtime includes Eigen under MPL-2.0; the requested ONNX blueprint is a
specific dependency-policy exception, recorded in the license register with
complete notices and a pinned Eigen source reference.

Analysis is capped at 2600 pixels on the longest dimension. DBNet receives BGR
NCHW ImageNet-normalized pixels and dimensions rounded to multiples of 32.
The v6 probability threshold is 0.2 and unclip ratio is 1.4, following its
published inference configuration; the library box-confidence default remains 50%.
Returned polygons are projected through the actual axis scales to the upright
source. The shortest oriented side gives line height, including vertical text;
box confidence and minimum 6px analysis height / 1.5 aspect ratio filter noise.
This geometry avoids an axis-aligned box growing with tilt, but does not promise
that detection is immune to rotation, perspective, or missed small text.

`StorageOptimization:TextDetector=tesseract` selects the existing TSV word
strategy instead. Only that strategy can filter punctuation and tokens with
fewer than two alphanumeric characters; DBNet detects regions without recognizing
characters. Defaults remain an interpolated fifth percentile, 12-pixel target,
50% confidence (DBNet box score or Tesseract word confidence), 0.05 minimum scale,
WebP quality 80, and no upscaling. Proposals record engine, model, strategy,
region count and analysis time. Line boxes include padding and are not equivalent
to word glyph heights. No reliable text or no byte savings means no approval is
requested. Decoder/OCR failures fail the workflow while keeping the source;
there is no silent fallback that would change the measurement strategy.

One processor per host process and a default 64-million-pixel decode limit bound
optimization work. `StorageOptimization:MaxPixels` and
`StorageOptimization:OcrTimeoutSeconds` configure resource limits. The existing
`Ocr:TesseractPath` configures the optional CLI.
`Ocr:PaddleModelPath` overrides the bundled detector with a
compatible DBNet ONNX model. `Ocr:PaddleThreads`
(default 1, range 1–32) controls CPU inference threads. The session is lazily
loaded once and disposed with the host; cancellation terminates ONNX inference.
CPU arena allocation and memory-pattern caching are disabled because varying
image shapes otherwise retain large buffers in a long-lived process. Native
model inference remains serialized with decoding/encoding by the process gate. Pending reviews have no deadline.
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
The optional Tesseract strategy implements word analysis independently. Both
engines require fresh benchmarks; quoted throughput from other hardware or
smaller analysis images is not an acceptance claim.
Word measurements, OCR prescaling, and compression do not prove preservation of
all text. Manager review remains mandatory; synthetic and real-image benchmark
results include a second OCR pass as diagnostic evidence.

The planned mixed-case extension ID was normalized to lowercase to satisfy the
existing manifest contract. Both database providers have migrations, and the
HTTP additions are included in all generated SDKs.

Receipt tag triggers use `itemUpdated.parameters.when` with a tag `added`
condition evaluated against the event snapshots, and an optional per-trigger `concurrency: "skip"` override.
Its effective value is saved with the run, so recovery applies the same policy.
Tag changes retain the active review and the later tag check observes the current
item. Upload triggers retain `replace`, cancelling reviews of obsolete uploads.
Concurrent scheduler initialization tolerates a competing insertion of the same
trigger state; durable occurrence IDs continue to prevent duplicate starts.

Approval forms and file reviews compose on the same approval node. A review may
also specify `inputSchema`; the comparison dialog renders the shared schema form
and sends its validated values with the decision. The server checks both review
freshness and the form, and later nodes read `{step:review.input.*}`. Ordinary
workspace approvals may omit an item; a file review still requires one.
