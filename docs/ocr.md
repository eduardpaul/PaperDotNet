# OCR services

`PaperDotNet.Ocr` owns offline PaddleOCR detection and recognition, optional
Tesseract recognition, and optional GLM recognition through Ollama. Documents
and extensions consume `PaperDotNet.Ocr.Contracts`; they do not depend on the
implementation assembly. The host registers OCR independently of tenant
extension enablement.

`IOcrService` recognizes ordered image streams and returns an owned searchable
PDF stream plus page text in the same order. `IOcrEngine` supports file-based
host processing. Input streams and input/output files remain caller-owned.
`ITextDetector` returns text-region heights projected to source dimensions;
`IWordLayoutDetector` exposes optional Tesseract word-layout detection.

## Default offline pipeline

`Ocr:Engine` defaults to `paddleocr`. The complete pipeline runs in-process on
CPU using bundled ONNX models; no Tesseract executable, Ollama server, network
access, runtime downloads, or installed fonts are needed:

1. `PP-OCRv6_small_det` locates text regions.
2. `ch_PP-LCNet_x0_25_textline_ori_cls_mobile` corrects upside-down text crops.
3. `PP-OCRv6_small_rec` reads text using its matching multilingual dictionary.
4. The PDF writer compresses the page image and adds invisible
   Unicode text at the detected positions. Accents, CJK characters and
   supplementary characters are preserved in the PDF and stored page text.

JPEG, PNG, WebP and ordered lists of page images are supported. PDF pages are
rendered by the existing Documents processor before recognition. EXIF
orientation and transparency are normalized before detection and PDF creation.
TIFF decoding is not included: convert TIFF documents to PNG/PDF, or explicitly
select `Ocr:Engine=tesseract` for TIFF processing. No additional image-decoding
package is added by the Paddle integration.

The multilingual recognizer does not use Tesseract language codes. The caller
still records document languages for search stemming. Photo to document uses
Paddle detection to calculate PDF image dimensions for a smallest reliable text
height of 32 px. The full-resolution normalized photo remains the OCR input;
blank pages keep their dimensions and page order.

Detection runs on a bounded preview and projects text polygons to the original
image. Recognition rectifies one crop at a time directly from the original
pixels, corrects its orientation, and applies the recognizer's 48 px input-height
preprocessing. The whole page is never shrunk for recognition.

After OCR finishes, the PDF writer resizes its visible image and scales the
recognized text coordinates with it. The PDF then embeds a quality-80 JPEG,
or lossless Flate if that is smaller (typically blank or flat pages). PDF supports
JPEG directly; the storage optimizer's WebP output cannot be embedded directly.
The 32 px photo conversion target also retains more pixels than the storage
optimizer's default 12 px target, so the percentage reduction depends on the photo.

## Settings and compatibility

Set `Ocr:Engine=tesseract` or `Ocr:Engine=glm` to select an alternative. Explicit
existing `Documents:Engine` settings remain compatibility fallbacks; `Ocr`
settings win. The default detector remains Paddle regardless of the configured
recognition engine. Storage optimization can explicitly select its legacy
word-layout strategy with `StorageOptimization:TextDetector=tesseract`.

Paddle settings:

- `Ocr:PaddleModelPath`: compatible detector model, default bundled small detector.
- `Ocr:PaddleRecognitionModelPath`: compatible CTC recognizer, default bundled small recognizer.
- `Ocr:PaddleDictionaryPath`: matching character dictionary. Class counts are checked before inference.
- `Ocr:PaddleClassifierModelPath`: text-line orientation classifier.
- `Ocr:PaddleThreads`: CPU inference threads (1–32, default 1).
- `Ocr:PaddlePdfImageQuality`: embedded JPEG quality (1–100, default 80).
- `Ocr:PaddleMaxDetectionDimension`: hard detector bound, default 1536 pixels;
  a multiple of 32 between 256 and 2600. Higher values increase native memory
  approximately with the image area. A separate one-megapixel inference bound
  lowers the effective dimension for square pages. Only detection uses this
  preview; recognition crops preserve source detail until model preprocessing.
  Crops are processed serially, capped at 16 million pixels and aspect ratio 128
  to bound both Skia buffers and the recognizer's CTC output tensor.
- `Ocr:OcrTimeout`: recognition timeout, including waiting for the shared processor.

Workflow messages have a four-minute execution timeout, below their five-minute
run lease. This accommodates serial multi-page CPU OCR without Wolverine's
[default 60-second message timeout](https://wolverinefx.net/guide/handlers/timeout)
repeatedly cancelling and restarting the activity. The effective OCR timeout in
a workflow is also bounded by that message timeout.

Legacy `StorageOptimization:PaddleModelPath` and `StorageOptimization:PaddleThreads`
remain detector/thread fallbacks. Official bundled detector preprocessing uses
ImageNet mean/std from its inference configuration, not the different
normalization used by RapidOCR's re-exported v6 models.

Alternative recognition settings are `Ocr:TesseractPath`, `Ocr:GlmBaseUrl`,
`Ocr:GlmModel`, `Ocr:GlmContext`, and `Ocr:GlmMaxTokens`. Documents retains file
processing settings such as OCR DPI and maximum pages.

OCR failure propagates to the caller. Blank recognized text is accepted only
when the caller requests it; missing models, mismatched dictionaries and
malformed images fail. Paddle sessions load lazily and serialize inference
across tenants and service providers. Detection and recognition share one
process-wide inference budget. PDF images stream directly to disk; the writer
does not retain all page image buffers. Extremely long, thin text lines are
rejected before recognition to bound CTC tensor memory; crop or split such pages. Temporary normalized pages are removed after each call.

On glibc Linux, start the host with `MALLOC_ARENA_MAX=2` and
`MALLOC_MMAP_THRESHOLD_=131072` (the default Docker image sets both). Limiting
allocator arenas and fixing the large-allocation threshold reduces native
buffer retention between pages ([glibc allocator documentation](https://sourceware.org/glibc/manual/latest/html_node/Memory-Allocation-Tunables.html)). These settings must be present when the process
starts; the .NET GC heap limit alone does not constrain ONNX, Skia or PDFium
allocations. When checking locally, run builds and OCR tests sequentially under
a process-group memory limit with swap disabled.
