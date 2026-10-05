# ADR-0048: Shared OCR module

Status: Accepted

OCR recognition lived inside Documents, while text detection lived inside the
storage optimization extension. Other modules and extensions should be able to
use these capabilities without depending on either implementation.

## Decision

`PaperDotNet.Ocr` owns Tesseract recognition and word-layout detection, GLM
recognition, Paddle text detection, detector model assets and their notices.
`PaperDotNet.Ocr.Contracts` exposes ordered-stream recognition, file-based
recognition and text detection. The host registers this module independently of
extension enablement. Documents and extensions use only its public contracts.

The bundled default detector is `PP-OCRv6_small_det`. It locates text regions;
it does not recognize characters. Recognition still defaults to Tesseract and
can use configured GLM. Choosing a recognition provider does not change the
default detector. Explicit custom compatible detector models remain supported.
The legacy v4 detector is no longer bundled.

The `Ocr` configuration section owns recognition and detection settings. Legacy
`Documents` recognition settings and `StorageOptimization` Paddle settings remain
fallbacks, with new `Ocr` settings taking precedence. Documents continues to own
file processing policy, such as rendering DPI, page limits, OCR language
preferences, live version changes and publishing.

## Consequences

Extensions can recognize or analyze staged content through the SDK without
implementing OCR or changing core modules. OCR results own their PDF stream;
callers own inputs. OCR failure propagates without modifying live documents.
Blank text is allowed only when explicitly requested. Detector inference is
serialized and the CPU model is loaded lazily.
