# Storage optimization benchmark — 2026-10-03

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
