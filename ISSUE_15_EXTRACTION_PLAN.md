# Issue 15: minimal explainable extraction plan

## Scope

The first query-plan prototype is an interpreted layer over `CompactDocument`. It deliberately does not parse CSS,
generate code, select a generic optimizer, or integrate with the tokenizer/tree builder.

Supported operations are:

- tag, ID, class-token, attribute-exists, and attribute-equals predicates;
- descendant and direct-child path steps;
- first and all cardinality;
- borrowed or owned attribute projection;
- owned normalized-subtree-text projection;
- required and optional field validation.

Plans bind to a document before repeated execution. Binding resolves all tag and attribute IDs once, keeping name lookup
and storage decisions outside the candidate loops. Execution uses preorder name-ID scans for descendant paths and
subtree-end jumps for direct children.

```csharp
var plan = CompactExtractionPlan
    .Start("div")
    .WithId("content")
    .TakeFirst()
    .SelectNormalizedText("text", required: true)
    .Compile();

var bound = plan.Bind(document);
var result = bound.Execute();
```

`Explain()` reports the execution mode, each path step, inspected/projected/materialized attributes, text retention,
sidecars, termination point, and estimated temporary state. `Requirements` exposes the same payload and metadata needs as
data. This prototype needs no metadata sidecars.

Attribute values are document-borrowed slices by default. They remain valid only while the compact document is alive.
Callers can request an owned attribute projection for values that must escape document disposal. Normalized text is
always owned because whitespace normalization materializes a new value. Every result value carries an explicit ownership
tag.

Required attribute projections reject a row only when the attribute is absent; an existing empty attribute remains a
valid value. Optional projections produce a field whose `Exists` flag is false. Counters report candidate nodes,
attributes inspected, matches, produced/rejected rows, and borrowed/owned values.

## Correctness

Tests cover frozen and packed compact layouts, every predicate/path/projection family, required versus optional fields,
empty attributes, ownership, and explain output. Malformed foster-parenting and formatting-adoption cases are compared
against AngleSharp's object DOM query results.

## Server-GC benchmark

The query-only benchmark uses one retained document and compares the representative
`first div#content -> normalized subtree text` query. It ran on .NET 10 with BenchmarkDotNet ShortRun and Concurrent
Server GC.

| Implementation | Mean | Allocated |
| --- | ---: | ---: |
| Read-only DOM traversal | 40.29 us | 9.58 KB |
| Hand-written compact scan | 21.95 us | 12.46 KB |
| Bound interpreted compact plan | 17.93 us | 12.92 KB |

The time ranking is directional because three-iteration ShortRun confidence intervals are wide. The stable result is that
the interpreted plan adds about 0.46 KB per execution over the hand-written compact result path while remaining in the
same performance class. Its bound IDs can avoid repeated name resolution, so interpretation is not inherently slower on
this representative query.

BenchmarkDotNet is now configured globally as `ShortRun + Server GC`, matching `BENCHMARKING.md` and `scripts/bench.ps1`.
Manual runners still fail fast if Server GC is unavailable.

## Current boundary

Path candidates are materialized between steps, with a duplicate-handle set for multi-parent paths. First cardinality
stops projection after the first valid row but currently evaluates the path candidate set first. This is stated in the
plan explanation rather than hidden.

The next useful measurement is a multi-row product-card plan. If candidate-list allocation dominates, specialize first
cardinality and single-parent paths before considering any general optimizer. Tokenizer/tree-builder integration remains
issue #17 and is not implied by this API.
