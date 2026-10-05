# Build-time only: uv run --with fonttools==4.66.1 tools/ocr/generate_text_font.py
# The font is used with invisible PDF text; one placeholder glyph covers every Unicode CID.
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
fb = FontBuilder(1000, isTTF=True)
fb.setupGlyphOrder([".notdef"])
fb.setupCharacterMap({})
p = TTGlyphPen(None)
p.moveTo((0, 0))
p.lineTo((1000, 0))
p.lineTo((1000, 1000))
p.lineTo((0, 1000))
p.closePath()
fb.setupGlyf({".notdef": p.glyph()})
fb.setupHorizontalMetrics({".notdef": (1000, 0)})
fb.setupHorizontalHeader(ascent=1000, descent=0)
fb.setupNameTable({
    "familyName": "PaperDotNet OCR Text",
    "styleName": "Regular",
    "uniqueFontIdentifier": "PaperDotNetOcrText",
    "fullName": "PaperDotNet OCR Text",
    "psName": "PaperDotNetOcrText",
})
fb.setupOS2(sTypoAscender=1000, sTypoDescender=0, usWinAscent=1000, usWinDescent=0)
fb.setupPost()
fb.setupMaxp()
fb.font["head"].created = fb.font["head"].modified = 3786912000
fb.save("src/Modules/Ocr/PaperDotNet.Ocr/models/OcrText.ttf")
