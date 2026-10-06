# Search indexing: infrastructure review and implementation

Date: 2026-10-06. Implemented on `feature/search-indexing-workflows`.

## Why search workflows were missing

Search had a backend abstraction but no registered search activities or built-in
workflows. Lists handled item events by writing the index directly; document and
comment writes called reindex directly; stores split text inside upsert; a
recurring embedding job performed provider calls. These paths bypassed durable
workflow runs and could only report indexing failures in transport/job logs.
The original ADR-0043 proposal also kept metadata always on and only disabled
file content, which would leave an excluded library's items searchable.

## Adaptation

The implementation uses the existing workflow engine for all item-processing
reactions. Search adds configurable item indexing, removal, container maintenance
and a bounded rebuild coordinator. The common engine now supports built-ins on
ordinary lists, immutable original item-event context, and completion signals
that connect child runs to durable waits. Other product item-event subscribers
were converted into activities registered as enabled built-in workflows.

```mermaid
flowchart LR
    Source[Lists / Documents / Collaboration] -->|events and requests| Engine[Workflow engine]
    Manual[Manual launch] --> Engine
    Engine --> Chunk[Stage current revision and chunks]
    Chunk --> Publish[Validate policy and publish]
    Publish --> Embed[Embed missing vectors]
    Publish --> Store[ISearchStore]
    Embed --> Store
    Query[Search API / MCP] -->|current ACL and inclusion filters| Store
```

List inclusion is independent of automatic execution: disabling automatic
indexing preserves manual runs, whereas exclusion hides all of a list's content
immediately and prevents indexing publication. Status compares current and
published source revisions and includes workflow, content and embedding state.

See [Search](search.md) for API/UI usage and
[ADR-0043](adr/0043-search-indexing-as-workflows-over-a-search-store.md) for
publication guarantees, provider boundaries and remaining tradeoffs.
