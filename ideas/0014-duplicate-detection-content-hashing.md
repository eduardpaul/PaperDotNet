# 0014: Duplicate detection and content hashing

- **Status:** new
- **Area:** Documents / Storage
- **Date:** 2026-09-24
- **Mapped to:**

## The idea

Compute a content hash (e.g. SHA-256) for every uploaded file and version.
Use it to:

- **Detect exact duplicates on upload**, with a configurable rule per library:
  allow, warn, link to the existing document, or reject.
- **Find near-duplicates**, such as a re-scan of the same paper: similar OCR
  text (e.g. MinHash/shingling) or close embeddings (idea 0013).
- **Store identical files once** (deduplicated storage): many items and
  versions can point to the same stored blob.

## Why / problem it solves

- The same invoice arrives by email, scanner and manual upload (ideas 0002
  and the consume folder).
- Papermerge docs name duplicate removal as a common request that core never
  solved.
- Saves storage for self-hosters.

## Examples / references

- Papermerge "Apps" documentation: duplicates by file name, digest or text
  similarity.
- Paperless-ngx rejects exact duplicates by checksum.

## Notes

<!-- Open questions to settle during review:
     - Scope of duplicate checks: per library, per workspace, or per tenant.
       Never across tenants: hashes must not leak whether another tenant has
       a file.
     - Dedup storage: reference counting on blobs; deletion only when the last
       reference goes (a background job). Interaction with retention and legal
       hold.
     - Near-duplicate detection as an extension point (the "apps" idea from
       Papermerge): extensions provide their own duplicate criteria.
     - API: `GET /items/{id}/duplicates`; duplicate info in upload responses.
     - Also useful for integrity checks: verify the stored file against its hash. -->
