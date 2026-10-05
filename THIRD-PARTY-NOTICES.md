# Third-party notices

PaperDotNet uses third-party components. Most are MIT or Apache-2.0 licensed;
the full register is [docs/dependency-licenses.md](docs/dependency-licenses.md).
Components under other permissive licenses that require their notice to be
reproduced are listed here.

## Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL, Npgsql.OpenTelemetry

PostgreSQL License. Copyright (c) 2002-2025, Npgsql Development Team.

> Permission to use, copy, modify, and distribute this software and its
> documentation for any purpose, without fee, and without a written agreement
> is hereby granted, provided that the above copyright notice and this
> paragraph and the following two paragraphs appear in all copies.
>
> IN NO EVENT SHALL THE NPGSQL DEVELOPMENT TEAM BE LIABLE TO ANY PARTY FOR
> DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, INCLUDING
> LOST PROFITS, ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION,
> EVEN IF THE NPGSQL DEVELOPMENT TEAM HAS BEEN ADVISED OF THE POSSIBILITY OF
> SUCH DAMAGE.
>
> THE NPGSQL DEVELOPMENT TEAM SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING,
> BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
> A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS ON AN "AS IS" BASIS,
> AND THE NPGSQL DEVELOPMENT TEAM HAS NO OBLIGATIONS TO PROVIDE MAINTENANCE,
> SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.

## Polly (via Microsoft.Extensions.Http.Resilience)

BSD 3-Clause License. Copyright (c) 2015-2024, App vNext.
See https://github.com/App-vNext/Polly/blob/main/LICENSE for the full text.

## PostgreSQL (runtime service, pgvector image)

PostgreSQL License. Copyright (c) 1996-2025, The PostgreSQL Global Development Group.
pgvector: PostgreSQL License, Copyright (c) 1996-2025, The PostgreSQL Global Development Group.

## PDFium (via PDFtoImage and bblanchon.PDFium)

BSD 3-Clause License. Copyright 2014 The PDFium Authors.
See https://pdfium.googlesource.com/pdfium/+/main/LICENSE for the full text.
PDFium includes third-party libraries under their own permissive licenses:
FreeType (FreeType License), libjpeg-turbo (IJG / BSD-3-Clause / zlib),
OpenJPEG (BSD-2-Clause), Little CMS (MIT) and zlib (zlib License); see
https://github.com/bblanchon/pdfium-binaries for the notices shipped with the binaries.

## Jint and Acornima

Jint: BSD 2-Clause License. Copyright (c) 2013, Sebastien Ros.
Acornima: BSD 3-Clause License. Copyright (c) Adam Simon.
See https://github.com/sebastienros/jint/blob/main/LICENSE.txt and
https://github.com/adams85/acornima/blob/master/LICENSE for the full texts.

## Skia (via SkiaSharp)

BSD 3-Clause License. Copyright (c) 2011 Google Inc.
See https://skia.googlesource.com/skia/+/main/LICENSE for the full text.

## Tesseract OCR and Leptonica (container image)

Tesseract: Apache License 2.0. Leptonica: BSD 2-Clause style license,
Copyright (C) 2001-2024 Leptonica. See http://leptonica.org/about-the-license.html.

## lucide-react (web UI icons)

ISC License. Copyright (c) 2026 Lucide Icons and Contributors.

> Permission to use, copy, modify, and/or distribute this software for any
> purpose with or without fee is hereby granted, provided that the above
> copyright notice and this permission notice appear in all copies.
>
> THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
> WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
> MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY
> SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
> WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION
> OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN
> CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

## @ungap/structured-clone (web UI, Markdown rendering)

> ISC License
>
> Copyright (c) 2021, Andrea Giammarchi, @WebReflection
>
> Permission to use, copy, modify, and/or distribute this software for any
> purpose with or without fee is hereby granted, provided that the above
> copyright notice and this permission notice appear in all copies.
>
> THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
> REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
> AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
> INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
> LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE
> OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
> PERFORMANCE OF THIS SOFTWARE.

## OCR and text detection

RapidOcrNet (Apache-2.0), copyright BobLd and RapidOCR contributors:
https://github.com/BobLd/RapidOcrNet
Based on RapidAI / RapidOCR and parts of PdfPig (Apache-2.0), and PContour /
PContourNet (MIT), copyright LingDong Huang / BobLd. See their notices:
https://github.com/BobLd/RapidOcrNet/blob/master/NOTICE.txt

PaddleOCR PP-OCRv6 small detection/recognition weights, character dictionary
and PP-LCNet text-line orientation classifier (Apache-2.0), copyright PaddlePaddle /
PaddleOCR contributors; published by PaddlePaddle. Model provenance
and complete license: src/Modules/Ocr/PaperDotNet.Ocr/models/.

Microsoft ONNX Runtime (MIT), copyright Microsoft Corporation:
https://github.com/microsoft/onnxruntime/blob/main/LICENSE
Native third-party notices:
https://github.com/microsoft/onnxruntime/blob/main/ThirdPartyNotices.txt

Clipper2 (Boost Software License 1.0), copyright Angus Johnson 2010–2025:
https://github.com/AngusJohnson/Clipper2/blob/main/LICENSE

## fast-uri (schema-driven workflow forms)

```text
Copyright (c) 2011-2021, Gary Court until https://github.com/garycourt/uri-js/commit/a1acf730b4bba3f1097c9f52e7d9d3aba8cdcaae
Copyright (c) 2021-present The Fastify team <https://github.com/fastify/fastify#team>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
    * Redistributions of source code must retain the above copyright
      notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright
      notice, this list of conditions and the following disclaimer in the
      documentation and/or other materials provided with the distribution.
    * The names of any contributors may not be used to endorse or promote
      products derived from this software without specific prior written
      permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDERS AND CONTRIBUTORS BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

                                  *   *   *

The complete list of contributors can be found at:
- https://github.com/garycourt/uri-js/graphs/contributors
```
