# Document optimization benchmark

Runs the production adapter with real PaddleOCR DBNet and a Tesseract OCR pass over the
candidate. No source image or output image is added to the repository.

```bash
dotnet run --project tests/benchmarks/document-optimization -- /path/to/images /tmp/results.json
dotnet run --project tests/benchmarks/document-optimization -- --synthetic /tmp/48mp-48mib.png
```

Set `TEXT_DETECTOR=tesseract` to benchmark the alternate word detector.
`analysisMilliseconds` isolates detector work (including first-call model loading),
while total elapsed time also includes decoding, encoding and verification.
DBNet line-region counts are not word counts and cannot be compared directly.

Set `TESSERACT_PATH` to a Tesseract executable or wrapper. Languages default to
English, matching the minimal container. Results include dimensions, measured
text height, scale, byte savings, detected word counts before/after, elapsed time
(including verification), and the .NET host's peak working set. The peak is a
process-wide high-water mark, not an incremental allocation measurement; it
excludes OCR processes or containers. Word counts are diagnostics, not a
text-retention guarantee. Synthetic file padding verifies upload byte limits;
it does not model the compression ratio of a real 48MiB camera photo.
