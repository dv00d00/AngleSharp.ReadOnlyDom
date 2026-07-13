# Compact DOM prototype

The isolated `AngleSharp.ReadOnlyDom.CompactPrototype` project tests direct HTML construction into a disposable,
document-scoped columnar representation. It does not change the production read-only DOM hierarchy.

## Surviving design

AngleSharp constructs through short-lived reference facades backed by a mutable arena. Finalization retains only:

- a 16-byte `CompactNode` core containing traversal links, payload index, name ID, kind, and compact flags;
- sparse `CompactNodePayload` records for nodes with values or attributes;
- one dense attribute arena reached through payload ranges;
- interned names and a shared character buffer;
- optional parent and source-location columns.

All construction and final document arrays are rented. `CompactDocument.Dispose()` returns them idempotently. The removed
owned/list-backed variants represented a different lifetime model and added branches throughout the hot construction path.
If a long-lived independently owned representation is required later, it should be an explicit deep copy rather than a
second construction mode.

`CompactParser` and reusable `CompactParserSession` accept caller `HtmlParserOptions`, a token-aware attribute predicate,
`TokenizerMiddleware`, and string, memory, or char-buffer input. Middleware and attribute filtering are predicate pushdown:
discarded subtrees and attributes never enter the arena.

Namespace and prefix values are derived, source-reference storage is conditional, template storage is lazy, and the
attribute arena is allocated only after the first accepted attribute.

## Current evidence

Anonymous query-level .NET 10 ShortRun workloads include parsing, predicate pushdown, materialization, query/checksum, and
disposal:

| Workload | Read-only | Compact | Read-only allocation | Compact allocation |
| --- | ---: | ---: | ---: | ---: |
| Selected subtree with sparse attributes | 148.47 us | 171.30 us | 59.51 KB | 35.77 KB |
| Attribute-free text subtree | 81.90 us | 91.04 us | 32.33 KB | 17.28 KB |

The compact path allocates materially less on these extraction shapes but remains 11-15% slower. Time and total managed
allocation are the optimization gates; retained footprint is recorded but is not itself a chase target.

An access-shape benchmark rejected density-only selection for optional columns. Binary-searching sparse payloads for every
node was 23-64 times slower than dense lookup across 1-90% density, while forward iteration over sparse values was efficient.
Use dense storage for per-node lookup and sparse storage for direct annotation enumeration; do not introduce a generic
runtime-switching abstraction without a concrete consumer requiring both modes.

## Removed experiments

Build-from-read-only conversion, exact-size double traversal, wide-versus-hot nodes, owned versus pooled buffers,
list-backed arena storage, selectable payload indexes, and wrapper-tree materialization were removed after answering their
questions. They are not supported alternatives.

Post-parse source-slice recovery was also rejected. AngleSharp token values do not consistently retain original-input backing
identity when source positions are disabled, so recovery required content searches. Allocation fell only 1.6-3.3 KB while
query time regressed roughly 19-22%. Source-backed values require token ranges at the AngleSharp construction boundary.

## Next optimization boundary

The remaining speed gap is primarily reachable preorder/remapping, per-document name interning, and text copying. The next
iteration should benchmark stable well-known tag and attribute IDs and investigate construction-order handles that can avoid
remapping while still excluding detached nodes. Changes remain gated by the anonymous query-level workloads.

The generic construction factory still has a known foreign-content correctness boundary where AngleSharp checks concrete
core element types. An upstream opaque-handle construction sink remains the clean architectural fix if this prototype moves
toward production.
