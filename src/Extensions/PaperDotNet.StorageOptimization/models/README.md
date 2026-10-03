# Bundled detection model

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

Normalization follows PaddleOCR's PP-OCRv4 inference pipeline: BGR, NCHW,
(pixel / 255 - [0.485, 0.456, 0.406]) / [0.229, 0.224, 0.225].
Resize each dimension to the nearest positive multiple of 32 and project each
axis back using its actual resize factor. DB postprocessing uses probability
threshold 0.3, configured box-score confidence, and unclip ratio 1.5.

References:
https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/tools/infer/predict_det.py
https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/ppocr/postprocess/db_postprocess.py
