# Storage optimization benchmarks — 2026-10-03

## DBNet default and same-container comparison

The current default is PaddleOCR PP-OCRv4 mobile DBNet, with the bundled model
and production adapter. Both detectors were run sequentially in the same local
runtime container with SkiaSharp 4.150.1, RapidOcrNet 4.2.0, ONNX Runtime 1.29.0,
Tesseract 5.3.4, English verification OCR, and the shipped tuning defaults.
The shared workstation exposes four logical Intel i7-10870H CPUs; each detector
uses one CPU inference thread. These are local measurements, not a server
throughput guarantee. The dataset remains outside the repository.

| Detector | Source bytes (19 receipt photos) | Candidate bytes | Aggregate savings | Median analysis | Median total with candidate OCR |
| --- | ---: | ---: | ---: | ---: | ---: |
| DBNet (line regions) | 136,454,657 | 3,171,990 | 97.68% | 6.51s | 9.09s |
| Tesseract (words) | 136,454,657 | 3,107,024 | 97.72% | 2.79s | 5.39s |

All 23 inputs (19 dated receipt photos, another receipt, and three synthetic POC
images) produced candidates without engine errors. Analysis time includes
preprocessing, detection and postprocessing, and model initialization on the
first DBNet call. It excludes full-source decoding, candidate encoding and the
verification pass. The total column includes those stages. DBNet was **slower**
here: these settings do not reproduce the proposed 40ms/page, 5–8× improvement,
or 150 pages/second claims. Smaller detector inputs, other hardware and worker
pools need their own measurements; they also change small-text detection.

DBNet's 19 candidate photos produced 1,762 accepted Tesseract verification words,
versus 1,694 with the Tesseract strategy. Detected DBNet regions and recognized
words are different units and must not be directly compared. Candidate word
height fifth percentiles ranged from 7–30px on the 18 photos with accepted
verification words; one photo returned **zero accepted words** (the word-based
candidate returned 10). Detection box height includes padding and does not
promise preservation of every glyph or OCR accuracy. No manual transcription or
readability score was collected for personal photos. Native manager review is
mandatory; this is diagnostic evidence, not an automated readability pass.

The initial DBNet run with native arena caching reached a process peak of
3,597,541,376 bytes (3,431MiB). Disabling CPU arena allocation and memory-pattern
caching reduced the final run to 2,685,771,776 bytes (2,561MiB), with identical
candidate bytes, dimensions, height measurements and verification word counts
for every input. Tesseract's same-container host peak was 784,093,184 bytes
(748MiB). These are process-wide high-water marks across images; they exclude
the Tesseract child process, and are not a hard memory cap. Large analysis images
still need substantial memory despite the one-processor and 64MP decode limits.

Raw, anonymized measurements: [DBNet](storage-optimization-dbnet-receipts.json)
and [Tesseract](storage-optimization-tesseract-container.json).
Select the alternate detector with
`PAPERDOTNET__StorageOptimization__TextDetector=tesseract`.

The separate 6000×8000 (48MP) synthetic PNG, padded to 50,331,648 bytes,
produced the same 411×549 WebP of 27,804 bytes as the word strategy. DBNet measured
36 regions; the candidate verification recognized all 80 known words at 12px.
Analysis took 6.91s and the complete run 8.52s; host peak was 1,307,717,632 bytes
(1,247MiB). This clean, large-text fixture tests byte and decode limits rather
than camera-photo compression or readability. See
[synthetic measurements](storage-optimization-dbnet-synthetic.json).

Validation with the DBNet default passed 200 unit tests, 106 architecture checks,
11 optimization/receipts integration cases on each database provider, and three
Playwright cases covering real 48MiB uploads, both decisions, native zoom/pan,
authenticated loading and stale review. Build, publish, formatting and web
checks passed. The Tesseract switch was exercised on both database providers.

## Earlier Tesseract wrapper baseline


The production word strategy was run against the adjacent POC's local sample
folder with SkiaSharp 4.150.1, Tesseract 5.3.4, English, and the shipped defaults.
Personal images remain outside the repository; JSON results anonymize photo names.
The engine ran through a local Docker wrapper with one OpenMP thread. Timings
include container startup, optimization, and a separate candidate OCR pass;
they are not production throughput measurements.

| Dataset | Source bytes | Candidate bytes | Aggregate savings | Median elapsed |
| --- | ---: | ---: | ---: | ---: |
| 19 dated receipt photographs | 136,454,657 | 3,107,024 | 97.72% | 35.4s |

Per-photo savings ranged from 81.71% to 99.45%, with a 98.37% median.
The second OCR pass measured candidate fifth-percentile word heights of 11–14px.
Across those photos, 2,128 accepted analysis words became 1,694 accepted candidate
words. Counts are influenced by confidence, segmentation, compression, and OCR
resolution; they do not measure exact character accuracy. They also show that
12px cannot promise preservation of every word. Review at native 100% remains
mandatory. No manual transcription/readability score was collected for personal
photos.

The full run included another receipt photo and three synthetic POC documents;
all 23 inputs produced candidates with no processing errors. The .NET host's
process-wide peak working set was 746,213,376 bytes (711.64MiB). It excludes the
OCR container and is a high-water mark across images, not per-image allocation.
See [raw measurements](storage-optimization-receipts.json).

The dedicated 48-megapixel synthetic image and the 48MiB upload acceptance tests
are separate checks. Synthetic padding tests byte limits, not the compression
ratio of a 48MiB camera photograph. The benchmark harness is documented in
[tests/benchmarks/document-optimization](../../tests/benchmarks/document-optimization/README.md).

The dedicated 6000×8000 (48MP) synthetic PNG, padded to 50,331,648 bytes,
produced a 411×549 WebP of 27,804 bytes in 18.5s including verification. Both
analysis and candidate OCR accepted 80 words; candidate text measured 12px.
Host peak working set was 615,288,832 bytes (586.79MiB). The source contains
large, clean text, so this extreme compression is not representative of camera
receipts. See [synthetic measurements](storage-optimization-synthetic.json).
