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
or `StorageOptimization:PaddleModelPath` to load a compatible DBNet model.

## Legacy: PP-OCRv4 mobile

The v4 weights remain bundled for explicit model-path overrides. The values
below describe the original v4 integration; the adapter now uses the v6
postprocessing thresholds for custom models as well. Historical benchmark
results retain their v4 model identity and are not v6 performance claims.

`ch_PP-OCRv4_det.onnx` is PaddleOCR's multilingual PP-OCRv4 mobile DBNet
detector, converted to ONNX by RapidAI / RapidOCR. It detects text regions only;
no recognition or orientation-classifier model is shipped.

Source (pinned release):
https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_mobile.onnx

SHA-256: `d2a7720d45a54257208b1e13e36a8479894cb74155a5efe29462512d42f49da9`

The digest is published in the upstream model registry:
https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml

Copyright PaddlePaddle / PaddleOCR contributors and RapidAI / RapidOCR
contributors. Distributed under Apache-2.0; see LICENSE. The model is copied
to build and publish output so inference does not need network access.

Legacy preprocessing follows PaddleOCR's PP-OCRv4 inference pipeline: BGR, NCHW,
(pixel / 255 - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225].
Resize each dimension to the nearest positive multiple of 32 and project each
axis back using its actual resize factor. The original v4 integration used probability
threshold 0.3, configured box-score confidence, and unclip ratio 1.5.

References:
https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/tools/infer/predict_det.py
https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/ppocr/postprocess/db_postprocess.py
