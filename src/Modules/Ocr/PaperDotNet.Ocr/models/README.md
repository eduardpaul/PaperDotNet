# Bundled detection models

## Default: PP-OCRv6 small

`PP-OCRv6_small_det.onnx` is the official PaddlePaddle PP-OCRv6 small DBNet
text detector. It detects regions only; recognition and classifier models are
not shipped. The 9,880,512-byte model is bundled for offline CPU inference.

Pinned upstream revision: `28fe5895c24fd108c19eb3e8479f4ab385fbfc62`

Source:
https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/resolve/28fe5895c24fd108c19eb3e8479f4ab385fbfc62/inference.onnx

SHA-256: `d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e`

Copyright PaddlePaddle / PaddleOCR contributors. Apache-2.0; see LICENSE.
The matching upstream inference configuration is preserved in
`PP-OCRv6_small_det.inference.yml`. Preprocessing uses BGR, NCHW, and
(pixel / 255 - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225].
The adapter uses probability threshold 0.2 and unclip ratio 1.4 from this
configuration; box confidence remains the library setting (default 50%).
Existing analysis bounds, polygon geometry filters, and percentile strategy apply.

Set `StorageOptimization:TextDetector=tesseract` to select TSV word detection,
or `Ocr:PaddleModelPath` to load a compatible DBNet model.

