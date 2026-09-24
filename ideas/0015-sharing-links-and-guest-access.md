# 0015: Sharing links and guest access

- **Status:** mapped
- **Area:** Security / Sharing
- **Date:** 2026-09-24
- **Mapped to:** [features.md](../docs/features.md): IAM-09…12

## The idea

Share items, folders or lists with people **outside the tenant**:

- **Links:**
  - anonymous or sign-in-required
  - expiry date and optional password
  - permission: view, download or edit
  - revocable any time
- **File request links:** upload-only links to collect documents from
  clients or partners into a chosen folder. The guest can't see anything else.
- **Guest users:** invited external accounts with access only to what was
  shared with them.

## Why / problem it solves

- Sending documents to an accountant, lawyer or client without email
  attachments.
- Collecting documents from others (tax papers, contracts) straight into the
  DMS.
- Standard in SharePoint/OneDrive, Nextcloud and Google Drive.

## Examples / references

- SharePoint/OneDrive: "Anyone", "People in your org", "Specific people"
  links; Graph `createLink` and `permissions` APIs (idea 0003).
- Nextcloud: file drop links.
- Papermerge sharing covers internal users/groups only (see
  [papermerge-features.md](../docs/papermerge-features.md) section 8).

## Notes

<!-- Open questions to settle during review:
     - Tenant policy: allow or deny anonymous links, maximum expiry, allowed
       permissions, domain allow-list for guests (idea 0005).
     - Link tokens: long random tokens, stored hashed; rate limiting on link
       endpoints; audit every access.
     - Guests are users in the tenant with a "guest" type and no default
       access, or a separate identity?
     - File request uploads: size and type limits, virus scan hook (file
       processing pipeline), land in Inbox for review.
     - Links point to a version or always the latest?
     - Events: `SharingLinkCreated`/`Accessed` for notifications (idea 0016)
       and event handlers (idea 0012). -->
