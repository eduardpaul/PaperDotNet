# OCR services

`PaperDotNet.Ocr` owns Tesseract recognition, GLM recognition through Ollama, and
Paddle text-region detection. Documents and extensions consume
`PaperDotNet.Ocr.Contracts`; they do not depend on the implementation assembly.
The host registers OCR independently of tenant extension enablement.

`IOcrService` recognizes ordered image streams and returns an owned searchable
PDF stream plus page text in the same order. `IOcrEngine` supports file-based
host processing. Input streams and input/output files remain caller-owned.
`ITextDetector` returns text-region heights projected to source dimensions;
`IWordLayoutDetector` exposes optional Tesseract word-layout detection.

The default detector is always the bundled `PP-OCRv6_small_det` ONNX model.
This is a detection-only model: it locates text but cannot recognize characters.
Recognition defaults to Tesseract; set `Ocr:Engine` to `glm` for GLM-OCR.
Detection remains PP-OCRv6 small regardless of the configured recognition engine.
Storage optimization can explicitly select its legacy Tesseract word-layout
strategy with `StorageOptimization:TextDetector=tesseract`.

Recognition settings are `Ocr:TesseractPath`, `Ocr:OcrTimeout`, `Ocr:Engine`,
`Ocr:GlmBaseUrl`, `Ocr:GlmModel`, `Ocr:GlmContext`, and `Ocr:GlmMaxTokens`.
`Ocr:PaddleModelPath` selects an explicitly supplied compatible detector;
`Ocr:PaddleThreads` controls CPU inference threads (1–32, default 1).
Existing `Documents` recognition settings and `StorageOptimization` Paddle
settings remain compatibility fallbacks; the corresponding `Ocr` settings win.
Documents retains file processing settings such as OCR DPI and maximum pages.

OCR failure propagates to the caller. Blank recognized text is accepted only
when the caller requests it; malformed, missing, or truncated GLM responses
still fail. Paddle sessions serialize concurrent inference and load lazily.
