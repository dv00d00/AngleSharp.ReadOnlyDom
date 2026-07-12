# Indexed compact DOM experiment

Issue #6 time-boxed an isolated representation experiment. The prototype lives in
`AngleSharp.ReadOnlyDom.CompactPrototype`; it does not change or subclass the production node hierarchy.

## Prototype shape

- 32-byte contiguous `CompactNode` records with integer first-child and next-sibling handles.
- 12-byte contiguous `CompactAttribute` records.
- Interned node and attribute names.
- One shared character array for text, comment, processing-instruction, and attribute values.
- Optional parallel parent and compact source-location arrays.
- No per-node wrapper objects. `CompactNodeWrapper` trees are materialized only on request.
- An exact sizing pass fills final arrays directly; the checked-in implementation is a build-then-compact experiment.

With `CompactMetadataOptions.None`, parent links and source locations do not exist. This is materially more minimal than
the production `Minimal` profile, whose `IConstructableNode` contract requires a parent reference in every object.

## .NET 10 results

BenchmarkDotNet ShortRun on the checked-in GitHub page:

| Operation | Mean | Allocated |
| --- | ---: | ---: |
| Parse read-only DOM | 5.279 ms | 967.81 KB |
| Compact an already parsed DOM | 0.950 ms | 1,300.55 KB |
| Parse then compact | 6.070 ms | 2,096.53 KB |

Build-then-compact therefore adds about 15% time and 117% allocation to this partial parse. It cannot be the cheapest parse
path because both representations coexist during conversion.

Traversal and compatibility costs on the same document:

| Operation | Mean | Allocated |
| --- | ---: | ---: |
| Recursive object-DOM traversal | 61.043 us | 109,840 B |
| Linked-handle compact traversal | 52.205 us | 136,776 B |
| Linear compact scan | 1.836 us | 0 B |
| Object-DOM `div` query | 101.076 us | 200 B |
| Compact `div` scan | 6.059 us | 0 B |
| Materialize wrapper tree | 185.388 us | 645,544 B |

Linear scans and simple tag selectors are highly feasible. A complete CSS selector engine would need a handle-oriented
adapter and, for repeated queries, optional indexes. Iterator-based linked traversal currently allocates; a struct enumerator
would remove that prototype artifact. Compatibility wrappers erase much of the allocation advantage and should remain opt-in.

Three-repetition five-page retained-memory results:

| Representation | Allocated | Retained | Approx. peak live | Retained / node |
| --- | ---: | ---: | ---: | ---: |
| Standard AngleSharp | 238.24 MB | 215.15 MB | 215.71 MB | 323.2 B |
| Read-only Minimal | 65.74 MB | 62.16 MB | 62.50 MB | 102.5 B |
| Compact, no metadata | 209.04 MB | 42.74 MB | 105.42 MB | 70.5 B |
| Compact + parents | 211.46 MB | 45.16 MB | 107.94 MB | 74.5 B |
| Compact + parents + source | 310.38 MB | 50.01 MB | 195.67 MB | 82.5 B |

On this corpus, compact Minimal retains 31% less than read-only Minimal. Parent handles cost about four bytes per node.
Compact source locations retain 66% less than the object SourceMapped representation (50.01 MB versus 147.62 MB), but the
temporary source-mapped DOM makes build allocation and peak memory substantially worse.

A one-repetition 47-page run retained 65.84 MB for compact Minimal versus 71.93 MB for read-only Minimal, an 8.5% saving.
It allocated 241.85 MB versus 80.62 MB and peaked at 131.46 MB versus 72.49 MB. The smaller retained win on the broad corpus
does not justify build-then-compact as the default parser path.

## Direct construction sink assessment

An AngleSharp upstream change is justified if compact parsing is pursued. The current generic builder cannot provide the
architectural ceiling because it requires reference objects implementing `IConstructableNode` and stores those objects in
its open-element and formatting stacks. It also:

- mutates `Parent` and object-backed child lists during construction;
- assigns source references through the element object;
- checks concrete core `HtmlTableElement` and `HtmlTemplateElement` types during foster parenting, which already causes a
  differential mismatch for custom constructable DOMs.

The smallest useful upstream direction is an opaque-handle construction sink where the tree builder owns stacks of an
unmanaged handle and calls sink operations such as create element, append/insert handle, set attributes, set source position,
and inspect tag/flags. The sink can use the parser stack during construction and omit the retained parent array for Minimal.
That would remove both the temporary object DOM and compatibility wrappers from the parse path.

## Target limitations

The isolated prototype builds on `net8.0` and `net10.0`; performance measurements are intentionally .NET 10 only. It does
not target .NET Framework 4.8.1 or `netstandard2.0`. The production hierarchy and existing tests remain supported there.
Porting the record structs and APIs is possible, but it would not answer the net10 architectural question and would add
polyfill noise to the experiment.

## Decision

Do not replace the production hierarchy with build-then-compact. Keep the prototype isolated.

The experiment proves that an indexed DOM can make Minimal genuinely smaller and can make scans dramatically faster, with
especially strong retained-memory savings for source metadata. The next architectural step, if these wins are valuable to
real consumers, is an upstream opaque-handle sink proposal followed by a direct-construction benchmark. Without that hook,
offer build-then-compact only as an explicit option for long-lived documents where retained memory outweighs temporary
allocation and peak-live cost.

## Direct factory-backed arena follow-up

A second prototype parses directly through AngleSharp's generic DOM construction factory into an indexed arena, then
finalizes that mutable arena into a 16-byte `HotCompactNode` array. Attributes, values, optional parents, and optional source
locations are cold storage. Parser-facing reference wrappers exist only while AngleSharp's tree builder is active and are not
retained by the returned `HotCompactDocument`.

This is an experiment, not a commitment to the 16-byte layout. The hot record intentionally contains only first-child,
next-sibling, cold-payload index, interned name ID, node kind, and one byte of hot flags. Any production contract requiring
all `NodeFlags` would need a separate cold column or a purpose-built compact flag contract.

### Short .NET 10 construction result

| Operation | Mean | Allocated |
| --- | ---: | ---: |
| Parse read-only Minimal | 4.963 ms | 860.55 KB |
| Parse read-only then 32-byte compact | 5.964 ms | 2,096.49 KB |
| Parse directly to arena then 16-byte hot compact | 5.928 ms | 2,485.33 KB |

The factory route removes coexistence with the read-only object DOM, but this first arena is not allocation-efficient. The
generic builder requires one reference wrapper per node, while the prototype also uses per-node mutable state and ordinary
`List<T>` growth storage. Direct construction matches build-then-compact time but allocates 19% more. This disproves the
assumption that avoiding the first DOM is sufficient by itself.

### Locality result

| Operation | Mean | Allocated |
| --- | ---: | ---: |
| Linear scan, 32-byte nodes | 1.841 us | 0 B |
| Linear scan, 16-byte hot nodes | 1.826 us | 0 B |
| `div` scan, wide node plus string lookup | 6.827 us | 0 B |
| `div` scan, hot node plus interned ID | 1.881 us | 0 B |
| Linked traversal, hot node plus parent column | 8.178 us | 0 B |

Halving the record did not materially improve a simple linear kind scan on this corpus. The large selector win combines the
smaller record with integer name IDs and removal of string lookup; it must not be attributed to record width alone. This
supports a hot/cold layout, but only where APIs and query execution consistently consume the hot fields.

### Retained-memory result

A one-repetition five-page retained run measured:

| Representation | Allocated | Retained | Approx. peak live | Retained / node |
| --- | ---: | ---: | ---: | ---: |
| Read-only Minimal | 66.03 MB | 62.16 MB | 62.40 MB | 102.5 B |
| Build-then-compact, no metadata | 209.04 MB | 42.74 MB | 105.45 MB | 70.5 B |
| Direct factory arena, 16-byte hot core | 292.05 MB | 40.67 MB | 249.07 MB | 67.0 B |

The final direct representation is the smallest measured result, about 5% below the 32-byte compact representation and 35%
below read-only Minimal. Construction allocation and sampled peak are currently unacceptable. Peak includes uncollected
construction garbage between documents, but that garbage is itself evidence of excessive transient work.

### Correctness boundary and next decision

The direct factory path is checked against AngleSharp's mutable core DOM on malformed formatting, implied elements, doctypes,
templates, entities, and broken comments. It also explicitly documents a known divergence for HTML inside SVG
`foreignObject`: AngleSharp's generic tree builder contains concrete core-element checks, so a custom construction factory
cannot reproduce the core tree in every case even when its own node operations are correct.

Do not select either the 16-byte layout or raw-token parsing as production architecture yet. The evidence supports this order:

1. Keep the hot/cold final representation as a promising query and retained-memory hypothesis.
2. Separately measure pooled construction scratch. A disposable document may optionally own rented final buffers, but that
   changes lifetime semantics and must be compared with exact-size owned arrays.
3. Prefer an upstream opaque-handle tree-builder sink. It retains AngleSharp's HTML tree-construction algorithms without the
   reference-wrapper and concrete-element constraints.
4. Consume raw tokenizer tokens only if the tree builder cannot be generalized. Tokens alone are not a DOM; this would mean
   owning adoption-agency, foster-parenting, template, foreign-content, and insertion-mode correctness.

The current factory arena is valuable evidence and a benchmark fixture, not a candidate default parser.

### Pooling and alternate-key follow-up

Both the original List-backed construction arena and an opt-in pooled arena are retained in the prototype. Likewise,
`CompactBufferOwnership.Owned` produces exact-size arrays while `Pooled` rents the final node, payload, attribute, name, text,
parent, and source buffers. `HotCompactDocument.Dispose()` idempotently returns rented buffers and clears reference and text
arrays before return.

On .NET 10, name interning now uses `Dictionary<string, ushort>.GetAlternateLookup<ReadOnlySpan<char>>()`. Lookup hashes the
source span without allocating; only a new distinct name becomes an owned string. The net8 build preserves the original
`Dictionary<StringOrMemory, ushort>` implementation. A frozen dictionary is intentionally not used because the table is
mutated during construction and discarded after finalization.

Successive short construction measurements isolated the effects:

| Direct variant | Mean | Allocated |
| --- | ---: | ---: |
| Owned, source-memory name keys | 5.964 ms | 2,485.43 KB |
| Pooled final buffers, source-memory name keys | 5.652 ms | 1,922.87 KB |
| Owned, alternate span name lookup | 5.838 ms | 2,189.74 KB |
| Pooled final buffers and alternate span lookup | 5.543 ms | 1,627.08 KB |
| Above plus pooled arena reference buffers, isolated rerun | 5.711 ms | 1.46 MB |

ShortRun timing noise is too wide to distinguish the last two pooled timings. Allocation is deterministic enough to show
that alternate span lookup saves about 296 KB on this document, final-buffer pooling saves about 563 KB after warmup, and
pooling the arena's two top-level reference buffers saves another roughly 160 KB. The remaining allocation is dominated by
per-node reference wrappers, `NodeState` objects, child lists, attributes, and finalization bookkeeping.

Pooling reverses direction for concurrently retained documents. In a one-repetition five-page retained run:

| Direct ownership | Allocated | Retained | Approx. peak live | Retained / node |
| --- | ---: | ---: | ---: | ---: |
| Exact owned arrays | 264.98 MB | 40.67 MB | 247.59 MB | 67.0 B |
| Pooled arrays and arena | 287.47 MB | 96.76 MB | 278.89 MB | 159.5 B |

Pool buckets are larger than the logical arrays, and buffers cannot be reused while all documents remain rooted. The pooled
mode is therefore appropriate only for sequential parse/use/dispose pipelines with reliable disposal. Exact owned arrays
remain the better default for long-lived documents or many documents alive concurrently. A deep optimization pass should
retain and separately measure both modes rather than generalizing from either workload.

### Fair parser reuse and small-collection pass

Reusable `DirectCompactParserSession` instances now put the direct and read-only construction benchmarks on the same parser
lifetime. Setup is reported separately instead of being charged only to direct parsing:

| Parser setup | Mean | Allocated |
| --- | ---: | ---: |
| Reused read-only parser factory call | 525 ns | 134 B |
| New direct owned session | 4.65 us | 17,440 B |
| New direct pooled session | 4.64 us | 17,440 B |

Reusing the direct session saves about 17 KB per parse but does not close the structural allocation gap. In the fair short
matrix, pooled direct construction used 1,507.7 KB versus 1,524.7 KB for the one-shot API and 905.7 KB for the temporarily
regressed read-only variant. Parser setup was therefore a benchmark defect, but not the root cause.

The collection pass tested owner-specific layouts rather than assuming one generic small-list policy:

- Making `ReadOnlyNamedNodeMap` allocate a nullable `List<T>` at attribute two regressed the GitHub parse from about 860 KB
  to 906 KB. The existing two-inline additional-attribute storage was restored.
- Empty direct-arena child and attribute lists are now null and allocate only on first mutation. On the five-page retained
  corpus this reduced direct owned construction allocation from 264.98 MB to 263.37 MB.
- `ReadOnlyNodeList` now stores four child references directly and allocates overflow only at child five. It is instantiated
  only when a node gains its second child, so leaf and singleton nodes do not pay for these fields.
- Four inline child slots reduced five-page read-only Minimal allocation from roughly 66.01 MB to 65.20 MB while increasing
  retained memory from 62.16 MB to 62.25 MB. This is the preferred trade for parse/extract/dispose workloads.

On x64, the preserved two-inline generic struct is 32 bytes and the four-inline variant is 48 bytes. Embedding four slots
directly in `ReadOnlyNodeList` yields an approximately 64-byte list object, avoiding separate `List<T>` and backing-array
objects for up to four children. Both generic variants remain in source as fixtures for the later deep optimization pass.
