# 0025: Text from Office and other files

- **Status:** new
- **Area:** Documents
- **Date:** 2026-10-07
- **Mapped to:**

## The idea

Read the text of files other than PDFs and images (Word, Excel, PowerPoint,
OpenDocument, plain text, e-mails) so that they are found in search, like PDFs.

## Why / problem it solves

Since WebDAV (ADR-0047) libraries take files of any type, but only PDFs and
images are read. Office files are found only by their title and fields.

## Examples / references

- SharePoint and Papermerge-like DMSs index Office content.
- A workflow activity per format (ADR-0038), e.g. `document.readText` reading
  Office Open XML with the .NET `System.IO.Packaging`/`DocumentFormat.OpenXml`
  (MIT), plain text directly.

## Notes

- Must stay a workflow step, never code in the upload.
- Thumbnails and page images for Office files would need a renderer
  (LibreOffice in a separate container?), which conflicts with the minimal
  install; text first.
