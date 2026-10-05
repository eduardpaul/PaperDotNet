# Bundled PaddleOCR models

## Default: PP-OCRv6 small

`PP-OCRv6_small_det.onnx` is the official PaddlePaddle PP-OCRv6 small DBNet
text detector. It detects regions only; the matching recognition and orientation models
listed below complete the OCR pipeline. The 9,880,512-byte model is bundled for offline CPU inference.

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


## Bundled recognition and orientation models

The default recognition engine is now the complete offline PaddleOCR pipeline:
detection, text-line orientation classification, CTC recognition, and a
positioned Unicode text layer in a searchable PDF. Tesseract is optional.

`PP-OCRv6_small_rec.onnx` (21,159,378 bytes), official PaddlePaddle export:

- Pinned upstream revision: `b8f84f0b80c529de40b4fbb3544b84fa7233a513`
- Source: https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/b8f84f0b80c529de40b4fbb3544b84fa7233a513/inference.onnx
- SHA-256: `5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634`
- Matching upstream configuration: `PP-OCRv6_small_rec.inference.yml`.
- `ppocrv6_small_dict.txt` contains the 18,708 `PostProcess.character_dict`
  entries from that configuration, one per UTF-8 line. RapidOcrNet adds the
  CTC blank and final space class. Never reorder or substitute this dictionary.
- Dictionary SHA-256: `b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d`

`ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` (1,018,508 bytes) is the
Paddle classifier redistributed by the already-installed RapidOcrNet 4.2.0:

- Pinned source: https://github.com/BobLd/RapidOcrNet/blob/708cae2fcb88720e1d891a81b5ee3e8b2bcc139e/RapidOcrNet/models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx
- SHA-256: `54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7`

All model weights and the dictionary are Apache-2.0, copyright PaddlePaddle /
PaddleOCR contributors; see LICENSE and the RapidOcrNet notices.

`OcrText.ttf` is a project-generated placeholder font used only for invisible
PDF text. The PDF embeds it and maps character IDs to Unicode with ToUnicode;
it does not rely on installed font coverage. Its reproducible build script is
`tools/ocr/generate_text_font.py` (run from the repository root). FontTools is a
build-time tool only, not an application dependency.
