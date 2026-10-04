# Workflow performance review

The review covers schema-based manual launches, workspace list events and tag
filters, run listing, and approval forms. Query counts are regression-tested;
request timings are local observations, not throughput or production capacity
claims.

## Changes

| Path | Before | Current behavior |
| --- | --- | --- |
| Event matching | Definition query, deduplication query, then version reads per matching workflow; the starter reread each version for concurrency | One joined definition/version query per trigger type, with deduplication in SQL; the starter reuses the matched spec |
| Workspace workflow listing | One workflow query plus one version read per workflow | One joined workflow/version query |
| Run listing and bulk launch responses | One name lookup per run | One batched name lookup |
| Manual selection | Repeated list metadata reads for each selected item | One metadata read per distinct list per launch |
| Item conditions | Item and synthetic tag triggers were matched in separate passes | One candidate/version query per item event; typed conditions evaluate snapshots in memory and taxonomy filters share a per-event cache |
| Form loading | Launch module loaded the form library on workspace and list pages | Shared renderer loaded when a launch or approval form opens; approval history forms open on request |

## Reproduction

```bash
PAPERDOTNET_WORKFLOW_PERF_OUTPUT="$(pwd)/artifacts/workflow-performance.json" \
  dotnet test --project tests/PaperDotNet.IntegrationTests -- \
  --filter-class '*WorkflowPerformanceTests'
```

The test uses a fresh SQLite database by default and the real integration host.
It seeds 1, 10, then 50 workspace workflows and directly delivers an item-updated
event with transactional before/after snapshots and a typed title condition. Each
delivery makes exactly one joined definition/version read. The test intercepts
item reads and queries to assert that parameterized conditions perform none. It
checks fan-out and repeats each event to verify deduplication.
The handler timer includes transactional run creation; background execution is not
awaited and still shares machine resources. The event is synthetic, so the timer
does not include item writes, taxonomy resolution, message transport, or library
default initialization. The report includes representative serialized JSON event
sizes for 1, 10, and 50 text fields of 128 characters, with both snapshots. These
sizes exclude transport envelopes and are not measurements of peak memory.

HTTP timings use the in-process test host, including authentication, database
work and response serialization, with no real network. Listings and manual
launches have five samples; the approval decision has one because a decision
can only succeed once. The first handler sample includes cold query compilation
and initialization; it should not be compared directly with warmed samples.

## Remaining costs

Creating and executing runs scales with the number of matches; batching reads
does not remove those writes or activity work. Workflow and run responses include
full definitions, schemas, inputs and outputs, so response size also matters.
Run and approval lists are paginated. Selected-item launches still check each
item's access independently and cap selections at 100. Non-parallel concurrency
modes and item conditions need additional per-item queries.

Trigger types remain stored as a comma-separated column. Matching multiple
trigger types can require prefix/suffix/substring checks within a workspace;
a normalized trigger table may be useful for much larger workflow catalogs.
Tag-category caches last only for the current event, keeping taxonomy changes
visible on subsequent events. Neither forms nor approval inputs introduce a
shared, unbounded schema cache.

Both SQLite and PostgreSQL migrations preserve existing item approvals while
adding nullable schema/response fields and allowing approvals without an item.
The performance sample below is SQLite; it does not establish PostgreSQL
latency or peak memory behavior.

## Parameterized-trigger sample (2026-10-04)

An isolated SQLite run after builds completed produced the following observations.
The first sample is cold; background workflow execution still shares host resources.

| Matching workflows | Definition/version reads | Item reads/condition queries | Handler time |
| --- | --- | --- | --- |
| 1 | 1 | 0 | 95.2 ms (cold) |
| 10 | 1 | 0 | 9.1 ms |
| 50 | 1 | 0 | 54.7 ms |

Median in-process HTTP times (five samples each) were 7.0 ms for workflow listing,
12.3 ms for run listing, 9.1 ms for manual launch, and 2.1 ms for approval listing.
The single approval decision took 55.3 ms. These observations do not establish a
latency improvement over the earlier run or production capacity.

| Snapshot fields | Characters per field | Serialized event bytes, including before/after |
| --- | --- | --- |
| 1 | 128 | 1,466 |
| 10 | 128 | 4,832 |
| 50 | 128 | 19,952 |

The fan-out workload's title-only event was 1,216 bytes regardless of the number
of workflows. Larger fields and snapshots also enlarge persisted run context and
API responses; the payload examples exclude transport framing and do not measure
peak memory. Taxonomy lookup costs are cached per distinct path within each event
and are outside this title-condition benchmark.

## Earlier local sample (2026-10-04, before parameterized triggers)

This earlier sample used two candidate queries per item/tag delivery.
The sample ran by itself after builds completed, using SQLite and the in-process
test host. No concurrent test classes or builds were running.

| Matching workflows | Definition/version reads | Handler time |
| --- | --- | --- |
| 1 | 2 | 160.6 ms (cold) |
| 10 | 2 | 8.7 ms |
| 50 | 2 | 49.8 ms |

| Request | Samples | Median | Maximum |
| --- | --- | --- | --- |
| workflow-list-50 | 5 | 5.2 ms | 32.6 ms |
| run-list-61 | 5 | 15.0 ms | 101.0 ms |
| manual-launch-with-schema | 5 | 7.0 ms | 28.0 ms |
| approval-list-with-schema | 5 | 1.8 ms | 5.5 ms |
| approval-decision-with-schema | 1 | 52.3 ms | 52.3 ms |

The one approval-decision sample is not a latency distribution. The small
sample sizes and test-host setup make these diagnostic figures, not a production
SLO or proof of an improvement in latency over the previous implementation.
The query-count reduction is verified independently by the assertions.

The production build moved the schema renderer into a separate 270.05 KB chunk
(89.79 KB gzipped), loaded when a form opens. The launch module changed from
278.90 KB (92.83 KB gzipped) to 9.30 KB (3.37 KB gzipped). These are individual
chunk sizes, not a measurement of total page transfer or browser memory.

The raw report is written to `artifacts/workflow-performance.json` when the
reproduction command's output variable is set.

Domain input forms keep choice queries out of the initial application bundle.
Relationship choices search readable items on demand (30 results per search);
term searches return up to 50 matches and explicit term choices use the batch
term endpoint. React Query shares and caches choice requests. Group term-set
choices follow pagination, so a later term set is not silently omitted.
Server validation batches terms per field and caches group membership per distinct
term set. Relationship targets receive individual permission checks, bounded at
100 IDs per field. Saved relationship labels likewise require individual item
reads; large selections therefore cost proportionally to the number of targets.
These are implementation bounds, not additional production timing measurements.
The build with domain pickers keeps the launch module at 9.23 KB (3.34 KB gzip);
the deferred schema renderer is 274.21 KB (91.30 KB gzip), about 4.16 KB larger
than the renderer measured above.


## Snapshot tradeoffs

Before/after fields and their type metadata increase outbox and execution-context
payload sizes linearly with item size. Values are detached from the entity before
mutation, so later writes cannot change matching results. The handler serializes
the event data once, evaluates condition groups with short-circuiting, and shares
term lookups within the event. No global condition cache grows with workflow edits.
Legacy workflow-level OData guards still query current values, and selected-item
launches and concurrency modes retain their existing independent costs.
