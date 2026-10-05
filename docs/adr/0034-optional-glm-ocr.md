# ADR-0034: Optional GLM-OCR image, Tesseract stays the default

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

OCR is the Tesseract CLI (ADR-0015). On a phone photo of a thermal receipt it
produced a mean word confidence of about 50: most prices survived as digits,
and the names, the tax table and the address did not. The reverse-side print
and the shop watermark were read as accents.

[GLM-OCR](https://github.com/zai-org/GLM-OCR) (0.9B, MIT weights, Apache-2.0
code) read that same photo through Ollama (`glm-ocr`, prompt
`Text Recognition:`). The names, the tax table and the ids came out; one unit
price digit and one watermarked letter did not. The run used about 5 GB on a
GPU. The same working set does not fit the small default machine's RAM, so
this cannot replace Tesseract in the one-container install.

## Decision

The default image and the default `Ocr:Engine` stay `tesseract`.

`Dockerfile.glm` is an optional image. It does not install Tesseract. It runs
Ollama 0.23.2 beside the app, with the `glm-ocr` model pulled at build time,
and sets `Ocr:Engine` to `glm`. Ollama listens on `127.0.0.1:11434`.
A GPU is used when the container has one; otherwise Ollama uses the CPU.

The app calls Ollama's `POST /api/generate` once per page (a JPEG or PNG as
itself, each TIFF frame as a PNG, or the existing list of rendered PDF pages).
Tesseract language codes are not sent. The library language is still stored,
so stemming is unchanged. The reply is plain text. Processing still stores a
searchable PDF: the page image plus an invisible text layer (PdfPig, rendering
mode "neither"). That layer is ASCII, with accents stripped, because the
standard PDF font has no other glyphs. Search uses the original text.

`Ocr:GlmContext` defaults to 16384. A smaller context truncates the
image and the call fails instead of storing the cut-off text.

## Consequences

- The minimal install is unchanged: one container, Tesseract, no extra model.
- Operators who want GLM-OCR build `Dockerfile.glm` (see
  `deploy/docker-compose.glm.yml`) and need a GPU or several gigabytes of RAM.
- A language Tesseract does not have fails the Tesseract engine. GLM-OCR
  ignores the language code and still reads the page.
