# Ideas

The inbox for raw ideas. Drop them here quickly, without worrying about
structure. Later, each idea gets reviewed and mapped to one or more features
(see [`docs/papermerge-features.md`](../docs/papermerge-features.md) and
[`docs/architecture-vision.md`](../docs/architecture-vision.md)).

## How to add an idea

1. Copy [`_template.md`](_template.md) to a new file named
   `NNNN-short-title.md`. Use the next free number, e.g. `0021-my-idea.md`.
2. Fill in at least **Title** and **The idea**. Every other section is optional.
   A single sentence is fine.
3. Add a row to the index below.

Too lazy for a file? Add a one-liner to [Quick ideas](#quick-ideas) at the bottom.
It will be turned into a file during review.

## Status lifecycle

| Status | Meaning |
|---|---|
| `new` | Just captured, not reviewed yet |
| `discussing` | Being clarified or refined |
| `mapped` | Linked to one or more features in the feature list or roadmap |
| `parked` | Good idea, not now |
| `rejected` | Won't do (reason noted in the file) |
| `done` | Implemented |

## Index

| # | Title | Area | Status | Mapped to ([features.md](../docs/features.md)) |
|---|---|---|---|---|
| [0001](0001-extensible-dms-productivity-platform.md) | Extensible DMS + productivity platform | Platform | mapped | Whole catalog; LST-01, EXT-01…09 |
| [0002](0002-email-to-inbox.md) | Email to inbox for documents | Documents | mapped | DOC-13 |
| [0003](0003-graph-style-api-and-generated-sdks.md) | Graph-style client API and generated multi-language SDKs | API / Integrations | mapped | API-01…06, LST-10, IAM-02 |
| [0004](0004-mcp-server.md) | MCP server (supporting the extensibility features) | API / Integrations / Extensions | mapped | API-08, API-09, IAM-02 |
| [0005](0005-ootb-multitenancy.md) | Out-of-the-box multitenancy support | Platform / Security | mapped | PLT-03…06, PLT-13, EXT-03, IAM-04 |
| [0006](0006-mobile-offline-sync.md) | Offline sync for mobile app | Mobile / Sync | mapped | API-05, API-12, LST-15, CAL-05 (mobile app itself deferred with UI) |
| [0007](0007-obsidian-vault-sync.md) | Obsidian vault sync | Integrations / Sync | mapped | API-11, LST-18 |
| [0008](0008-taxonomy-folksonomy-metadata-service.md) | Taxonomy and folksonomy (managed metadata service) | Platform / Metadata | mapped | TAX-01…07, TAX-11 |
| [0009](0009-automation-rules-engine-elsa.md) | Automation rules engine (tags and other use cases) with Elsa Workflows | Automation / Extensions | mapped | EVT-07…09, TSK-06 |
| [0010](0010-optional-version-history.md) | Optional (opt-in) version history for data | Platform / Data | mapped | LST-11, LST-12 |
| [0011](0011-extensions-in-nodejs-and-python.md) | Extensions implemented in Node.js and Python | Extensions | mapped | EXT-01, EXT-08 |
| [0012](0012-sharepoint-style-event-handlers.md) | Event handlers like SharePoint event receivers (before / after) | Extensions / Platform | mapped | EVT-01…04 |
| [0013](0013-unified-fulltext-and-vector-search.md) | Full-text search across all data types, plus vector search | Search / Platform | mapped | SRC-01…10, AI-05 |
| [0014](0014-duplicate-detection-content-hashing.md) | Duplicate detection and content hashing | Documents / Storage | mapped | DOC-10…12 |
| [0015](0015-sharing-links-and-guest-access.md) | Sharing links and guest access | Security / Sharing | mapped | IAM-09…12 |
| [0016](0016-notifications-alerts-subscriptions.md) | Notifications, alerts and subscriptions | Platform / Notifications | mapped | NTF-01…06, API-06 |
| [0017](0017-ai-metadata-extraction.md) | AI extraction of metadata into custom fields | AI / Documents | mapped | AI-01…04, AI-06, TSK-06 |
| [0018](0018-webdav-access-to-libraries.md) | WebDAV access to libraries | Integrations / Documents | mapped | API-10 |
| [0019](0019-caldav-carddav-server.md) | CalDAV / CardDAV server for tasks, calendar and contacts | Integrations / Calendar / Tasks | mapped | CAL-05, CAL-06 |
| [0020](0020-smart-folders.md) | Smart folders (more than saved searches) | Platform / Navigation / Metadata | mapped | TAX-08…10, TSK-03 |

## Quick ideas

<!-- One line per idea: `- YYYY-MM-DD: idea text` -->
