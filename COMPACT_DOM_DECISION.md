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
