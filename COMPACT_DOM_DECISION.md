# Compact DOM

`AngleSharp.ReadOnlyDom.Compact` provides direct HTML construction into a disposable, document-scoped columnar
representation. It remains independent from the object-oriented read-only DOM hierarchy.

## Surviving design

AngleSharp constructs through short-lived reference facades backed by a mutable arena. The default result is a frozen view
over that arena, not a second materialized tree:

1. AngleSharp finishes all construction calls against the reference facades.
2. The arena verifies that document order still matches construction order and no detached nodes remain.
3. Node and attribute names are interned during construction, and logical text length is maintained incrementally.
4. Reference-facade buffers are released; the document takes ownership of the arena columns and input source.
5. `CompactDocument` accessors synthesize the same public node, payload, and attribute views directly from the columns.
6. `CompactDocument.Dispose()` returns the arena columns and name-ID buffers and disposes the source.

The frozen representation releases construction-only columns and wrapper buffers, then keeps the query-facing arena
columns until disposal. This avoids a second traversal and copy. Parent and sibling mutation links are no longer exposed
after ownership transfer.

If HTML tree construction detached, reparented, or reordered nodes, the parser automatically falls back to packed
finalization so unreachable nodes cannot leak into the visible traversal. Callers can also request
`CompactDocumentLayout.Packed` explicitly. Packed finalization performs reachable preorder traversal, remaps handles, and
copies only final columns into these buffers:

- a 16-byte `CompactNode` core containing traversal links, payload index, name ID, kind, and compact flags;
- sparse `CompactNodePayload` records for nodes with values or attributes;
- one dense attribute arena reached through payload ranges;
- interned names and a shared character buffer;
- optional parent and source-location columns.

All construction and document arrays are rented. `CompactDocument.Dispose()` returns them idempotently. Packed layout is
appropriate when a document will be retained long enough for locality and smaller retained size to repay the copy, or when
the final representation must be independent of the construction arena and source. It is not the default extraction path.
The removed owned/list-backed variants represented a different lifetime model and added branches throughout construction.
If a permanently owned representation is required later, it should be an explicit deep copy.

`CompactParser` mirrors the read-only DOM integration: it exposes cached construction contexts and creates reusable
`HtmlParser` instances whose `ParseCompactDocument` extensions accept `TokenizerMiddleware` and string, memory,
char-buffer, or `TextSource` input. Parser creation accepts caller `HtmlParserOptions` and a token-aware attribute
predicate. Middleware and attribute filtering are predicate pushdown: discarded subtrees and attributes never enter the
arena.

Namespace and prefix values are derived, source-reference storage is conditional, template storage is lazy, and the
attribute arena is allocated only after the first accepted attribute.

## Current evidence

Anonymous query-level .NET 10 ShortRun workloads include parsing, predicate pushdown, materialization, query/checksum, and
disposal:

| Workload | Read-only | Frozen | Packed | Read-only allocation | Frozen allocation | Packed allocation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Selected subtree with sparse attributes | 150.04 us | 159.55 us | 167.83 us | 59.51 KB | 22.56 KB | 35.80 KB |
| Attribute-free text subtree | 81.34 us | 84.08 us | 88.99 us | 32.33 KB | 10.66 KB | 17.31 KB |

Frozen construction is about 6% slower than read-only on the selected-subtree workload and 3% slower on text extraction,
while allocating 62% and 67% less respectively. Explicit packing costs another 5-6% time and 6.7-13.2 KB per parse in
these workloads. Time and total managed allocation are the optimization gates; retained footprint is recorded but is not
itself a chase target. These are ShortRun measurements, so use them as directional gates rather than precision claims.

The three largest checked-in HTML documents add a parse-only scale check. Their benchmark labels are anonymous; the cases
represent approximately 13.5 MB of dense markup, 2.1 MB of attribute-heavy markup, and 1.5 MB with relatively few nodes and
substantial embedded content. All three retain frozen layout and produce matching element counts, including template
contents:

| Document | AngleSharp | Read-only | Frozen | AngleSharp allocation | Read-only allocation | Frozen allocation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| LargeA | 1,193.86 ms | 479.29 ms | 395.87 ms | 218.86 MB | 60.62 MB | 27.98 MB |
| LargeB | 47.28 ms | 19.29 ms | 21.06 ms | 14.15 MB | 2.64 MB | 1.59 MB |
| LargeC | 11.81 ms | 7.40 ms | 9.05 ms | 7.39 MB | 0.50 MB | 0.41 MB |

Frozen is 17% faster than read-only on the node-dense case, but 9% and 22% slower on the two lower-density cases. It still
allocates 54%, 40%, and 17% less than read-only respectively. This argues against optimizing exclusively for the giant
dense document: name processing and arena construction overhead remain visible when input bytes do not materialize into
many nodes.

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

An earlier name-ID experiment added a second dynamically grown column and used generated linear dispatch during mutation;
that version regressed the query workloads. The surviving design instead stores IDs directly in the arena's existing name
column and uses the generated known-name dictionary plus a per-document custom-name table, eliminating the freeze re-walk.

The surviving hybrid generates 305 stable `ushort` IDs from AngleSharp's canonical tag and attribute constants. A small
per-document lookup cache contains only names encountered by that document; standard entries point at process-wide strings,
and only unknown names enter the document-owned custom-name list. The text length is maintained during construction rather
than rescanned at publication. Follow-up frozen-only ShortRun results were 161.24 us / 22.37 KB for selected-subtree parsing
and 83.90 us / 10.50 KB for text extraction, effectively preserving time while shaving roughly 0.2 KB per operation.

`CompactParserProfiles.Extraction` consolidates the safe default skips for comments, processing instructions, script text,
raw/style text, frames, source references, position tracking, and preserved attribute casing. Attribute predicate pushdown
remains caller-specific. SVG/Math subtree suppression is deliberately not included: it needs a tree-builder policy rather
than unsafe token dropping.

## Next optimization boundary

The default path no longer performs reachable preorder/remapping, text copying, standard-name string allocation, a text
length scan, or a publication-time name-ID pass. Removing the per-document name lookup cache would require a pooled
specialized lookup that beats the current dictionary, not a broad generated switch. The larger remaining capability
boundary is safe foreign-content suppression in AngleSharp's tree builder.

The compact construction path uses stable arena-backed facades at AngleSharp's existing generic builder boundary. The full
Packed and FrozenColumns smoke matrix covers templates, foreign content, foster parenting, and formatting adoption. A new
upstream opaque-handle sink is therefore optional future wrapper-removal work, not a correctness prerequisite.
